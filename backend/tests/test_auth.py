"""Account sign-up, log-in and token checks, run against the in-memory store (no MongoDB needed)."""

from datetime import UTC, datetime, timedelta

import pytest
from fastapi.testclient import TestClient

from src.api.routes.training_v2 import get_training_store
from src.app import app
from src.auth import service as auth_service
from src.auth.passwords import hash_password, verify_password
from src.auth.service import AuthService
from src.auth.store import MemoryAccountStore, StoreUnavailable
from src.core.training_telemetry import TrainingTrace
from src.persistence.training_store import TrainingStoreError


class Clock:
    def __init__(self) -> None:
        self.now = datetime(2026, 10, 26, 12, 0, tzinfo=UTC)

    def __call__(self) -> datetime:
        return self.now


@pytest.fixture
def clock() -> Clock:
    return Clock()


@pytest.fixture
def client(clock: Clock):
    auth_service.set_auth_service(AuthService(MemoryAccountStore(), clock))
    try:
        yield TestClient(app)
    finally:
        auth_service.set_auth_service(None)
        app.dependency_overrides.clear()


def sign_up(client: TestClient, username: str = "Rishika", password: str = "long-password-1") -> dict:
    response = client.post("/api/auth/signup", json={"username": username, "password": password})
    assert response.status_code == 201, response.text
    return response.json()


def bearer(token: str) -> dict:
    return {"Authorization": f"Bearer {token}"}


def test_sign_up_logs_in_and_token_identifies_the_user(client):
    session = sign_up(client)
    assert session["username"] == "Rishika"
    assert session["expires_in"] == 12 * 60 * 60
    me = client.get("/api/auth/me", headers=bearer(session["token"]))
    assert me.status_code == 200
    assert me.json() == {"username": "Rishika"}


def test_log_in_ignores_username_case_and_returns_the_stored_name(client):
    sign_up(client)
    response = client.post("/api/auth/login", json={"username": "rishika", "password": "long-password-1"})
    assert response.status_code == 200
    assert response.json()["username"] == "Rishika"


def test_usernames_are_unique_regardless_of_case(client):
    sign_up(client)
    response = client.post("/api/auth/signup", json={"username": "RISHIKA", "password": "another-password"})
    assert response.status_code == 409
    assert response.json()["error"]["code"] == "username_taken"


@pytest.mark.parametrize("username, password", [
    ("ab", "long-password-1"),
    ("has space", "long-password-1"),
    ("valid_name", "short"),
])
def test_sign_up_rejects_bad_details(client, username, password):
    response = client.post("/api/auth/signup", json={"username": username, "password": password})
    assert response.status_code == 400
    assert response.json()["error"]["code"] == "invalid_account_details"


def test_wrong_password_and_unknown_user_look_the_same(client):
    sign_up(client)
    wrong = client.post("/api/auth/login", json={"username": "Rishika", "password": "wrong-password"})
    unknown = client.post("/api/auth/login", json={"username": "nobody", "password": "long-password-1"})
    assert wrong.status_code == unknown.status_code == 401
    assert wrong.json() == unknown.json()


def test_log_out_ends_the_session(client):
    token = sign_up(client)["token"]
    assert client.post("/api/auth/logout", headers=bearer(token)).status_code == 204
    assert client.get("/api/auth/me", headers=bearer(token)).status_code == 401


def test_expired_and_unknown_tokens_are_not_logged_in(client, clock):
    token = sign_up(client)["token"]
    clock.now += timedelta(hours=12)
    assert client.get("/api/auth/me", headers=bearer(token)).status_code == 401
    assert client.get("/api/auth/me", headers=bearer("made-up-token")).status_code == 401
    assert client.get("/api/auth/me").status_code == 401


def test_expired_token_on_a_training_route_asks_to_log_in_again(client, clock):
    class Store:
        trace = TrainingTrace()

        def snapshot(self, session_id):
            raise AssertionError("the request must stop before reaching the training store")

    app.dependency_overrides[get_training_store] = lambda: Store()
    token = sign_up(client)["token"]
    clock.now += timedelta(hours=12)
    # 401 session_expired, not the anonymous 403 that clients treat as a final rejection
    response = client.get("/api/v2/sessions/s1/snapshot", headers=bearer(token))
    assert response.status_code == 401
    assert response.json()["error"]["code"] == "session_expired"
    assert response.json()["error"]["retryable"] is True


def test_training_routes_accept_a_logged_in_user(client):
    class Store:
        trace = TrainingTrace()

        def runtime_ids(self):
            return []

        def snapshot(self, session_id):
            raise TrainingStoreError("INVALID_REFERENCE", "no such session")

    app.dependency_overrides[get_training_store] = lambda: Store()
    token = sign_up(client)["token"]
    # Logged in: gets past the identity check to the (missing) session
    assert client.get("/api/v2/sessions/s1/snapshot", headers=bearer(token)).status_code == 404
    # Not logged in: refused before the session is looked up
    denied = client.get("/api/v2/sessions/s1/snapshot")
    assert denied.status_code == 403
    assert denied.json()["error"]["code"] == "ROLE_DENIED"


def test_account_outage_is_retryable_instead_of_logged_out(client, clock):
    class Store:
        trace = TrainingTrace()

        def snapshot(self, session_id):
            raise AssertionError("the request must stop before reaching the training store")

    class OfflineStore(MemoryAccountStore):
        def find_session(self, token_hash):
            raise StoreUnavailable("cluster unreachable")

    app.dependency_overrides[get_training_store] = lambda: Store()
    auth_service.set_auth_service(AuthService(OfflineStore(), clock))
    # 403 would make clients drop a command the backend may have accepted; 503 makes them retry
    response = client.get("/api/v2/sessions/s1/snapshot", headers=bearer("some-token"))
    assert response.status_code == 503
    assert response.json()["error"]["code"] == "accounts_unavailable"
    assert response.json()["error"]["retryable"] is True
    # Requests without a token never consult the database, so they are still plainly anonymous
    assert client.get("/api/v2/sessions/s1/snapshot").status_code == 403


def test_unreachable_cluster_at_start_up_is_retryable_not_a_server_error(client, monkeypatch):
    from pymongo.errors import ConfigurationError

    from src.auth import store as store_module

    def unresolvable(*args, **kwargs):
        raise ConfigurationError("The DNS query name does not exist")

    auth_service.set_auth_service(None)
    settings = auth_service.get_settings().model_copy(update={"mongodb_uri": "mongodb+srv://u:p@cluster.invalid/"})
    monkeypatch.setattr(auth_service, "get_settings", lambda: settings)
    monkeypatch.setattr(store_module, "MongoClient", unresolvable)

    login = client.post("/api/auth/login", json={"username": "Rishika", "password": "long-password-1"})
    assert login.status_code == 503
    assert login.json()["error"]["code"] == "accounts_unavailable"
    with_token = client.get("/api/auth/me", headers=bearer("some-token"))
    assert with_token.status_code == 503
    assert with_token.json()["error"]["retryable"] is True


def test_auth_endpoints_report_missing_configuration(client, monkeypatch):
    auth_service.set_auth_service(None)
    settings = auth_service.get_settings().model_copy(update={"mongodb_uri": None})
    monkeypatch.setattr(auth_service, "get_settings", lambda: settings)
    response = client.post("/api/auth/login", json={"username": "Rishika", "password": "long-password-1"})
    assert response.status_code == 503
    assert response.json()["error"]["code"] == "accounts_unavailable"


def test_password_records_never_contain_the_password_and_reject_tampering():
    record = hash_password("long-password-1")
    assert "long-password-1" not in str(record)
    assert verify_password(record, "long-password-1")
    assert not verify_password(record, "long-password-2")
    assert not verify_password({**record, "salt": record["salt"][:-2]}, "long-password-1")
    assert not verify_password({**record, "password_format": "plain"}, "long-password-1")
