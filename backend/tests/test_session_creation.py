"""Real HTTP admission, shared lifecycle, durability and private-state recovery."""

import asyncio
from concurrent.futures import ThreadPoolExecutor
from datetime import UTC, datetime, timedelta
import hashlib
import json
import re
import sqlite3
from threading import Event as ThreadEvent
from uuid import uuid4

import pytest
from fastapi.testclient import TestClient

from contracts.v2.models import DispatcherCommand
from contracts.v2.sessions import CreateSessionRequest
from src.api.routes.training_v2 import get_training_store
from src.app import app
from src.auth import service as auth_service
from src.auth.service import AuthService
from src.auth.store import MemoryAccountStore
from src.core.config import get_settings
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.services.training_sessions import TrainingSessionService, readable_session_id

TOKEN = "session-creation-test-credential"
ACTOR = "training-operator"
BODY = {"scenario_revision_id": "scenario-m2-r1", "seed": 1000}
HEADERS = {"Authorization": f"Bearer {TOKEN}", "Idempotency-Key": "attempt-one"}


def log_in_test_actor():
    # A logged-in account whose token is TOKEN, kept in memory so no MongoDB is needed
    store = MemoryAccountStore()
    now = datetime.now(UTC)
    store.add_session({"token_hash": hashlib.sha256(TOKEN.encode()).hexdigest(), "username": ACTOR,
                       "created_at": now, "expires_at": now + timedelta(hours=1)})
    auth_service.set_auth_service(AuthService(store))


async def wait_until(predicate):
    async with asyncio.timeout(5):
        while not predicate():
            await asyncio.sleep(0.005)


async def manual_clock(self, session):
    # Keep an admitted runtime alive while a test explicitly chooses its steps.
    await asyncio.Event().wait()


@pytest.fixture
def api(tmp_path, monkeypatch):
    log_in_test_actor()
    get_settings.cache_clear()
    with TrainingStore(tmp_path / "sessions.sqlite") as store:
        app.dependency_overrides[get_training_store] = lambda: store
        try:
            with TestClient(app) as client:
                yield client, store
        finally:
            app.dependency_overrides.clear()
            auth_service.set_auth_service(None)
            get_settings.cache_clear()


def create(client, **overrides):
    return client.post("/api/v2/sessions", headers=HEADERS, json={**BODY, **overrides})


def control(client, snapshot, kind, key=None):
    return client.post(f"/api/v2/sessions/{snapshot['session_id']}/commands", headers=HEADERS, json={
        "contract_version": "2.0", "command_id": str(uuid4()), "idempotency_key": key or str(uuid4()),
        "session_id": snapshot["session_id"], "command_type": kind,
        "expected_epoch": snapshot["session_epoch"], "expected_revision": snapshot["session"]["aggregate_revision"],
        "target_type": "SESSION", "target_id": snapshot["session_id"], "payload": {}, "reason": None,
    })


def test_create_is_durable_ready_and_retries_share_one_actor(api):
    client, store = api
    with ThreadPoolExecutor(max_workers=2) as pool:
        responses = list(pool.map(lambda _: create(client), range(2)))
    assert [r.status_code for r in responses] == [201, 201]
    assert responses[0].json() == responses[1].json()
    session_id = responses[0].json()["session_id"]
    snapshot = client.get(f"/api/v2/sessions/{session_id}/snapshot", headers=HEADERS).json()
    assert snapshot["session"]["status"] == "READY"
    assert snapshot["tick"] == 0 and len(snapshot["vehicles"]) == 5
    assert store.snapshot(session_id).model_dump(mode="json") == snapshot
    assert snapshot["scenario"]["revision_id"].startswith("fleet-scenario-")
    assert "rng" not in snapshot and "shuttles" not in snapshot
    service = app.state.training_services[store]
    assert len(service.sessions) == 1
    assert create(client, seed=42).status_code == 409
    assert len(service.sessions) == 1


def test_authentication_and_invalid_admission(api):
    client, store = api
    assert client.post("/api/v2/sessions", json=BODY).status_code == 403
    # An unknown or expired login token is told to log in again
    wrong = {"Authorization": "Bearer wrong", "Idempotency-Key": "x"}
    assert client.post("/api/v2/sessions", headers=wrong, json=BODY).status_code == 401
    assert create(client, scenario_revision_id="missing").status_code == 404
    for seed in [-1, True, "1000", 2147483648]:
        assert create(client, seed=seed).status_code == 422
    assert client.post("/api/v2/sessions", headers={"Authorization": f"Bearer {TOKEN}"}, json=BODY).status_code == 422
    assert create(client, actor_id="forged").status_code == 422
    assert not store.runtimes()


def test_session_list_requires_login_and_returns_empty_capacity(api):
    client, store = api
    assert client.get("/api/v2/sessions").status_code == 403
    assert client.get("/api/v2/sessions", headers={"Authorization": "Bearer invalid"}).status_code == 401
    response = client.get("/api/v2/sessions", headers=HEADERS)
    assert response.status_code == 200
    assert response.json() == {"sessions": [], "live_count": 0, "total": 0, "capacity": 16, "terminal_practice_count": 0}


def test_a_session_keeps_the_name_it_was_created_with(api):
    """The name is display text in the list: the ID stays the key, and a retry keeps the first name."""
    client, store = api
    named = create(client, name="Morning drill")
    assert named.status_code == 201
    unnamed = client.post("/api/v2/sessions", headers={**HEADERS, "Idempotency-Key": "unnamed"}, json=BODY)
    listed = {s["session_id"]: s["name"] for s in client.get("/api/v2/sessions", headers=HEADERS).json()["sessions"]}
    assert listed == {named.json()["session_id"]: "Morning drill", unnamed.json()["session_id"]: None}
    assert create(client, name="Morning drill").json()["session_id"] == named.json()["session_id"]
    assert create(client, name="Evening drill").status_code == 409
    for bad in ["", " padded ", "two\nlines", "x" * 65, 7]:
        assert client.post("/api/v2/sessions", headers={**HEADERS, "Idempotency-Key": "bad-name"},
                           json={**BODY, "name": bad}).status_code == 422


def archived_attempts(client, store, count):
    ids = []
    for index in range(count):
        session_id = client.post("/api/v2/sessions", headers={**HEADERS, "Idempotency-Key": f"archived-{index}"},
                                 json=BODY).json()["session_id"]
        assert control(client, store.snapshot(session_id).model_dump(mode="json"), "ARCHIVE").json()["status"] == "APPLIED"
        ids.append(session_id)
    return ids


def test_session_list_pages_keep_whole_table_counts_and_live_attempts_first(api, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    client, store = api
    archived = archived_attempts(client, store, 3)
    live = create(client).json()["session_id"]
    path = "/api/v2/sessions"
    first = client.get(path, headers=HEADERS, params={"limit": 2}).json()
    assert [s["session_id"] for s in first["sessions"]] == [live, archived[2]]
    assert (first["total"], first["live_count"]) == (4, 1)
    second = client.get(path, headers=HEADERS, params={"limit": 2, "offset": 2}).json()
    assert [s["session_id"] for s in second["sessions"]] == [archived[1], archived[0]]
    assert (second["total"], second["live_count"]) == (4, 1)
    for params in ({"limit": 0}, {"limit": 501}, {"offset": -1}):
        assert client.get(path, headers=HEADERS, params=params).status_code == 422


def test_purge_keeps_newest_terminal_and_every_live_attempt(api, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    client, store = api
    archived = archived_attempts(client, store, 3)
    live = create(client).json()["session_id"]
    path = "/api/v2/sessions/purge"
    assert client.post(path, params={"keep_latest": 1}).status_code == 403
    assert client.post(path, headers=HEADERS).status_code == 422
    assert client.post(path, headers=HEADERS, params={"keep_latest": -1}).status_code == 422
    response = client.post(path, headers=HEADERS, params={"keep_latest": 1})
    assert response.status_code == 200 and response.json() == {"deleted_count": 2}
    assert [s["session_id"] for s in store.list_sessions()] == [live, archived[2]]
    with sqlite3.connect(store.path) as connection:
        for table in ("session_creations", "commands", "events", "checkpoints"):
            assert connection.execute(f"SELECT COUNT(*) FROM {table} WHERE session_id=?", (archived[0],)).fetchone()[0] == 0
    assert client.post(path, headers=HEADERS, params={"keep_latest": 1}).json() == {"deleted_count": 0}
    assert client.post(path, headers=HEADERS, params={"keep_latest": 0}).json() == {"deleted_count": 1}
    assert [s["session_id"] for s in store.list_sessions()] == [live]


@pytest.mark.parametrize("keep_latest, expected", [(None, 3), (1, 1)])
def test_retention_setting_prunes_terminal_history_at_startup_only_when_opted_in(tmp_path, monkeypatch, keep_latest, expected):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)

    async def exercise():
        with TrainingStore(tmp_path / "retention.sqlite") as store:
            first = TrainingSessionService(store)
            for index in range(3):
                snapshot = await first.create(CreateSessionRequest(**BODY), ACTOR, f"attempt-{index}")
                await first.submit(ACTOR, DispatcherCommand(
                    contract_version="2.0", command_id=str(uuid4()), idempotency_key=str(uuid4()),
                    session_id=snapshot.session_id, command_type="ARCHIVE", expected_epoch=snapshot.session_epoch,
                    expected_revision=snapshot.session.aggregate_revision, target_type="SESSION",
                    target_id=snapshot.session_id, payload={}, reason=None))
            await first.close()
            newest = store.list_sessions()[0]["session_id"]
            second = TrainingSessionService(store, retention_keep_latest=keep_latest)
            await second.initialize()
            await second.close()
            listed = store.list_sessions()
            assert len(listed) == expected and listed[0]["session_id"] == newest

    asyncio.run(exercise())


def test_failed_retention_does_not_block_startup(tmp_path, monkeypatch, caplog):
    with TrainingStore(tmp_path / "retention-failure.sqlite") as store:
        def fail(_):
            raise TrainingStoreError("QUEUE_FULL", "Injected retention failure")

        monkeypatch.setattr(store, "expired_session_ids", fail)

        async def exercise():
            service = TrainingSessionService(store, retention_keep_latest=1)
            await service.initialize()
            assert (await service.create(CreateSessionRequest(**BODY), ACTOR, "after")).session.status == "READY"
            await service.close()

        asyncio.run(exercise())
    assert "Training retention failed" in caplog.text


def test_session_list_includes_history_and_updates_capacity_after_archive(api, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    monkeypatch.setattr(TrainingSessionService, "MAX_SESSIONS", 2)
    client, store = api
    first = create(client).json()["session_id"]
    second = client.post("/api/v2/sessions", headers={**HEADERS, "Idempotency-Key": "second"},
                         json={**BODY, "seed": 42}).json()["session_id"]
    path = "/api/v2/sessions"
    full = client.get(path, headers=HEADERS).json()
    assert full["live_count"] == full["capacity"] == 2
    assert [s["session_id"] for s in full["sessions"]] == [second, first]
    assert all(s["occupies_capacity"] and s["status"] == "READY" for s in full["sessions"])
    assert all(set(s) == {"session_id", "name", "scenario_revision_id", "status", "occupies_capacity",
                          "is_assessment", "can_delete", "can_archive"}
               for s in full["sessions"])
    baseline = store.snapshot(second).model_dump(mode="json")
    assert control(client, baseline, "ARCHIVE").json()["status"] == "APPLIED"
    after = client.get(path, headers=HEADERS).json()
    assert after["live_count"] == 1
    assert after["sessions"] == [full["sessions"][1], {**full["sessions"][0], "status": "ARCHIVED",
                                                       "occupies_capacity": False}]
    assert client.post(path, headers={**HEADERS, "Idempotency-Key": "replacement"}, json=BODY).status_code == 201


def test_session_list_does_not_count_unmanaged_fixture_as_live(api):
    from pathlib import Path
    from contracts.v2.models import FullSnapshot

    client, store = api
    fixture = Path(__file__).resolve().parents[2] / "contracts/v2/fixtures/snapshot.json"
    store.create_session(FullSnapshot.model_validate_json(fixture.read_text(encoding="utf-8")))
    listed = client.get("/api/v2/sessions", headers=HEADERS).json()
    assert listed["live_count"] == 0
    assert len(listed["sessions"]) == 1
    assert listed["sessions"][0]["session_id"] == "session-001"
    assert listed["sessions"][0]["occupies_capacity"] is False


@pytest.mark.parametrize("scenario", ["scenario-m2-r1", "scenario-m2-road-3d-r1"])
def test_start_pause_resume_and_two_observers_share_committed_state(api, monkeypatch, scenario):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    client, store = api
    created = create(client, scenario_revision_id=scenario).json()
    session_id = created["session_id"]
    path = f"/api/v2/sessions/{session_id}"
    read = lambda: client.get(path + "/snapshot", headers=HEADERS).json()
    baseline = read()
    assert bool(baseline.get("spatial")) == (scenario == "scenario-m2-road-3d-r1")
    service = app.state.training_services[store]
    live = service.sessions[session_id]
    client.portal.call(service.step, live)
    assert read()["tick"] == 0  # READY never advances.
    result = control(client, baseline, "START", "start-once")
    assert result.status_code == 202 and result.json()["status"] == "APPLIED"
    replay = control(client, baseline, "START", "start-once")
    assert replay.json()["command_id"] == result.json()["command_id"]
    client.portal.call(service.step, live)
    client.portal.call(service.step, live)
    first, second = read(), read()
    assert first["tick"] > 0 and first["session"]["status"] == "RUNNING"
    assert first["session_id"] == second["session_id"]
    assert len(service.sessions) == 1
    assert control(client, first, "PAUSE").json()["status"] == "APPLIED"
    paused = read()
    client.portal.call(service.step, live)
    assert read()["tick"] == paused["tick"]
    assert control(client, paused, "RESUME").json()["status"] == "APPLIED"
    assert control(client, baseline, "PAUSE").status_code == 409
    events = client.get(path + "/events?after=0", headers=HEADERS).json()["events"]
    assert [e["event_type"] for e in events].count("SESSION_RUNNING") == 2
    assert [e["event_sequence"] for e in events] == list(range(1, len(events) + 1))
    assert client.get(path + "/commands/start-once", headers=HEADERS).json()["status"] == "APPLIED"
    assert client.get(path + "/snapshot").status_code == 403


def test_capacity_rejects_new_attempt_without_breaking_creation_replay(api, monkeypatch):
    client, store = api
    monkeypatch.setattr(TrainingSessionService, "MAX_SESSIONS", 1)
    first = create(client)
    assert first.status_code == 201
    rejected = client.post("/api/v2/sessions", headers={**HEADERS, "Idempotency-Key": "another"}, json=BODY)
    assert rejected.status_code == 503 and rejected.json()["error"]["code"] == "QUEUE_FULL"
    assert create(client).json()["session_id"] == first.json()["session_id"]
    assert len(store.runtimes()) == 1


@pytest.mark.parametrize("status", ["READY", "RUNNING", "PAUSED"])
def test_permanent_delete_requires_archive_and_authentication(api, monkeypatch, status):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    client, store = api
    session_id = create(client).json()["session_id"]
    if status != "READY":
        control(client, store.snapshot(session_id).model_dump(mode="json"), "START")
    if status == "PAUSED":
        control(client, store.snapshot(session_id).model_dump(mode="json"), "PAUSE")
    path = f"/api/v2/sessions/{session_id}"
    assert client.delete(path).status_code == 403
    response = client.delete(path, headers=HEADERS)
    assert response.status_code == 400 and response.json()["error"]["code"] == "INVALID_TRANSITION"
    assert store.snapshot(session_id).session.status == status
    assert app.state.training_services[store].owns(session_id)
    assert client.post("/api/v2/sessions/compact", headers=HEADERS).status_code == 400


def test_permanent_delete_removes_history_receipts_creation_keys_and_list_entry(api, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    client, store = api
    session_id = create(client).json()["session_id"]
    path = f"/api/v2/sessions/{session_id}"
    snapshot = store.snapshot(session_id).model_dump(mode="json")
    assert control(client, snapshot, "ARCHIVE", "archive-once").json()["status"] == "APPLIED"
    survivor = client.post("/api/v2/sessions", headers={**HEADERS, "Idempotency-Key": "survivor"}, json=BODY).json()
    with ThreadPoolExecutor(max_workers=2) as pool:
        responses = list(pool.map(lambda _: client.delete(path, headers=HEADERS), range(2)))
    assert [response.status_code for response in responses] == [204, 204]
    assert all(response.content == b"" for response in responses)
    assert client.get(path + "/snapshot", headers=HEADERS).status_code == 404
    assert client.get(path + "/events", headers=HEADERS).status_code == 404
    assert client.get(path + "/commands/archive-once", headers=HEADERS).status_code == 404
    listed = client.get("/api/v2/sessions", headers=HEADERS).json()
    assert [session["session_id"] for session in listed["sessions"]] == [survivor["session_id"]]
    assert listed["live_count"] == 1
    with sqlite3.connect(store.path) as connection:
        for table in ("sessions", "session_creations", "session_runtimes", "commands", "events", "checkpoints"):
            assert connection.execute(f"SELECT COUNT(*) FROM {table} WHERE session_id=?", (session_id,)).fetchone()[0] == 0
        assert connection.execute("PRAGMA foreign_key_check").fetchall() == []
    assert store.snapshot(survivor["session_id"]).session.status == "READY"
    # Deletion also forgets creation idempotency; this is a new attempt.
    assert create(client).json()["session_id"] != session_id


def test_compact_is_authenticated_and_returns_disk_usage_after_delete(api):
    client, store = api
    session_id = create(client).json()["session_id"]
    assert control(client, store.snapshot(session_id).model_dump(mode="json"), "ARCHIVE").status_code == 202
    assert client.delete(f"/api/v2/sessions/{session_id}", headers=HEADERS).status_code == 204
    assert client.post("/api/v2/sessions/compact").status_code == 403
    result = client.post("/api/v2/sessions/compact", headers=HEADERS)
    assert result.status_code == 200
    usage = result.json()
    assert usage["database_bytes_after"] < usage["database_bytes_before"]
    assert usage["wal_truncated"] is True
    assert client.get("/api/v2/sessions", headers=HEADERS).json()["sessions"] == []


def test_compaction_holds_admission_and_survives_caller_disconnect(tmp_path, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    started, release = ThreadEvent(), ThreadEvent()
    with TrainingStore(tmp_path / "compact.sqlite") as store:
        original = store.compact

        def blocked_compact():
            started.set()
            assert release.wait(5)
            return original()

        monkeypatch.setattr(store, "compact", blocked_compact)

        async def exercise():
            service = TrainingSessionService(store)
            try:
                compact = asyncio.create_task(service.compact())
                await wait_until(started.is_set)
                compact.cancel()
                with pytest.raises(asyncio.CancelledError):
                    await compact
                creation = asyncio.create_task(service.create(CreateSessionRequest(**BODY), ACTOR, "new"))
                await asyncio.sleep(0)
                assert not creation.done() and not service.sessions
                release.set()
                assert (await creation).session.status == "READY"
            finally:
                release.set()
                await service.close()

        asyncio.run(exercise())


@pytest.mark.parametrize("status", ["READY", "RUNNING", "PAUSED"])
def test_archive_releases_capacity_and_preserves_history_and_retries(api, monkeypatch, status):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    monkeypatch.setattr(TrainingSessionService, "MAX_SESSIONS", 1)
    client, store = api
    session_id = create(client).json()["session_id"]
    read = lambda: store.snapshot(session_id).model_dump(mode="json")
    if status != "READY":
        assert control(client, read(), "START").json()["status"] == "APPLIED"
    if status == "PAUSED":
        assert control(client, read(), "PAUSE").json()["status"] == "APPLIED"
    service = app.state.training_services[store]
    live = service.sessions[session_id]
    baseline = read()
    stale = {**baseline, "session": {**baseline["session"], "aggregate_revision": 99}}
    assert control(client, stale, "ARCHIVE").status_code == 409
    assert service.owns(session_id) and not live.task.done()
    assert client.post("/api/v2/sessions", headers={**HEADERS, "Idempotency-Key": "new"}, json=BODY).status_code == 503
    with ThreadPoolExecutor(max_workers=2) as pool:
        archives = list(pool.map(lambda _: control(client, baseline, "ARCHIVE", "archive-once"), range(2)))
    archived = archives[0]
    assert archived.status_code == 202 and archived.json()["status"] == "APPLIED"
    assert archives[1].json()["command_id"] == archived.json()["command_id"]
    client.portal.call(wait_until, live.task.done)
    assert live.task.cancelled() and not service.owns(session_id)
    result = client.get(f"/api/v2/sessions/{session_id}/snapshot", headers=HEADERS)
    assert result.status_code == 200 and result.json()["session"]["status"] == "ARCHIVED"
    assert result.json()["tick"] == baseline["tick"]
    retry = control(client, baseline, "ARCHIVE", "archive-once")
    assert retry.json()["command_id"] == archived.json()["command_id"]
    assert [e.event_type for e in store.replay(session_id, 0).events].count("SESSION_ARCHIVED") == 1
    assert create(client).json()["session_id"] == session_id
    assert create(client).json()["status"] == "ARCHIVED"
    assert client.post("/api/v2/sessions", headers={**HEADERS, "Idempotency-Key": "new"}, json=BODY).status_code == 201
    assert len(service.sessions) == 1 and len(store.runtimes()) == 1


def test_failed_archive_commit_keeps_runtime_and_capacity_until_retry(api, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    monkeypatch.setattr(TrainingSessionService, "MAX_SESSIONS", 1)
    client, store = api
    session_id = create(client).json()["session_id"]
    baseline = store.snapshot(session_id).model_dump(mode="json")
    service = app.state.training_services[store]
    live = service.sessions[session_id]
    apply = store.apply

    def fail(*_, **__):
        raise TrainingStoreError("QUEUE_FULL", "Injected archive commit failure")

    monkeypatch.setattr(store, "apply", fail)
    assert control(client, baseline, "ARCHIVE", "retry-archive").status_code == 503
    assert service.owns(session_id) and not live.task.done()
    assert store.snapshot(session_id).session.status == "READY"
    assert not store.replay(session_id, 0).events
    assert client.post("/api/v2/sessions", headers={**HEADERS, "Idempotency-Key": "new"}, json=BODY).status_code == 503
    monkeypatch.setattr(store, "apply", apply)
    assert control(client, baseline, "ARCHIVE", "retry-archive").json()["status"] == "APPLIED"
    assert not service.owns(session_id)
    assert client.post("/api/v2/sessions", headers={**HEADERS, "Idempotency-Key": "new"}, json=BODY).status_code == 201


def test_archive_settles_another_clients_queued_command(api, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    client, store = api
    session_id = create(client).json()["session_id"]
    baseline = store.snapshot(session_id).model_dump(mode="json")
    apply = store.apply

    def fail(*_, **__):
        raise TrainingStoreError("QUEUE_FULL", "Injected failure after command acceptance")

    monkeypatch.setattr(store, "apply", fail)
    assert control(client, baseline, "START", "pending-start").status_code == 503
    pending = store.command_status(session_id, ACTOR, "pending-start")
    assert pending.status == "QUEUED"
    monkeypatch.setattr(store, "apply", apply)
    write_snapshot = store._write_snapshot
    monkeypatch.setattr(store, "_write_snapshot", fail)
    assert control(client, baseline, "ARCHIVE", "archive").status_code == 503
    assert store.command_status(session_id, ACTOR, "pending-start").status == "QUEUED"
    assert store.snapshot(session_id).session.status == "READY" and not store.replay(session_id, 0).events
    monkeypatch.setattr(store, "_write_snapshot", write_snapshot)
    assert control(client, baseline, "ARCHIVE", "archive").json()["status"] == "APPLIED"
    result = control(client, baseline, "START", "pending-start").json()
    assert result["status"] == "EXPIRED" and result["command_id"] == pending.command_id
    assert result["failure_code"] == "INVALID_TRANSITION" and result["result_event_id"]
    events = store.replay(session_id, 0).events
    assert [e.event_type for e in events] == ["SESSION_ARCHIVED", "COMMAND_EXPIRED", "COMMAND_APPLIED"]
    assert [e.event_sequence for e in events] == [1, 2, 3]


def test_archived_attempt_does_not_reclaim_capacity_after_restart(tmp_path, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    monkeypatch.setattr(TrainingSessionService, "MAX_SESSIONS", 1)

    async def exercise():
        database = tmp_path / "archive.sqlite"
        request = CreateSessionRequest(**BODY)
        with TrainingStore(database) as store:
            service = TrainingSessionService(store)
            try:
                snapshot = await service.create(request, "operator", "first")
                command = DispatcherCommand(
                    contract_version="2.0", command_id="archive", idempotency_key="archive",
                    session_id=snapshot.session_id, command_type="ARCHIVE", expected_epoch=1, expected_revision=0,
                    target_type="SESSION", target_id=snapshot.session_id, payload={},
                )
                assert (await service.submit("operator", command)).status == "APPLIED"
            finally:
                await service.close()
        with TrainingStore(database) as store:
            service = TrainingSessionService(store)
            try:
                await service.initialize()
                assert not service.sessions
                assert (await service.submit("operator", command)).status == "APPLIED"
                assert (await service.create(request, "operator", "first")).session.status == "ARCHIVED"
                assert (await service.create(request, "operator", "second")).session.status == "READY"
            finally:
                await service.close()

    asyncio.run(exercise())


@pytest.mark.parametrize("damage", ["hash", "json", "missing-field", "version", "revision", "rng", "clock", "mismatch", "missing-runtime"])
def test_startup_isolates_corrupt_actor_checkpoint_and_initializes_healthy_sessions(tmp_path, monkeypatch, damage):
    log_in_test_actor()
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    get_settings.cache_clear()
    database = tmp_path / "corrupt.sqlite"

    async def prepare(store):
        service = TrainingSessionService(store)
        try:
            bad = await service.create(CreateSessionRequest(**BODY), "operator", "bad")
            healthy = await service.create(CreateSessionRequest(**BODY), "operator", "healthy")
            legacy = healthy.model_copy(deep=True)
            legacy.session_id = legacy.session.session_id = "non-live-session"
            store.create_session(legacy)
            return bad.session_id, healthy.session_id
        finally:
            await service.close()

    with TrainingStore(database) as store:
        bad_id, healthy_id = asyncio.run(prepare(store))
    with sqlite3.connect(database) as connection:
        raw = connection.execute("SELECT checkpoint_json FROM session_runtimes WHERE session_id=?", (bad_id,)).fetchone()[0]
        state = json.loads(raw)
        if damage == "hash":
            connection.execute("UPDATE session_runtimes SET state_hash='bad' WHERE session_id=?", (bad_id,))
        elif damage == "missing-runtime":
            connection.execute("DELETE FROM session_runtimes WHERE session_id=?", (bad_id,))
        else:
            if damage == "missing-field":
                del state["seed"]
            elif damage == "version":
                state["version"] = 99
            elif damage == "revision":
                state["scenario_revision"] = "unavailable"
            elif damage == "rng":
                state["rng"] = []
            elif damage == "clock":
                state["command_tick"] = "invalid"
            elif damage == "mismatch":
                state["tick"] += 2
            raw = "{" if damage == "json" else json.dumps(state)
            connection.execute("UPDATE session_runtimes SET checkpoint_json=?, state_hash=? WHERE session_id=?",
                               (raw, hashlib.sha256(raw.encode()).hexdigest(), bad_id))
    try:
        with TrainingStore(database) as store:
            app.dependency_overrides[get_training_store] = lambda: store
            with TestClient(app) as client:
                # Lifespan must initialize recovery before the first request.
                service = app.state.training_services[store]
                assert service._initialized and set(service.sessions) == {healthy_id}
                assert not service.sessions[healthy_id].task.done()
                assert store.snapshot(bad_id).session.status == "FAILED"
                failure = [e for e in store.replay(bad_id, 0).events if e.event_type == "SESSION_FAILED"]
                assert len(failure) == 1 and failure[0].data["reason"] == "CORRUPT_CHECKPOINT"
                for session_id in (bad_id, healthy_id, "non-live-session"):
                    assert client.get(f"/api/v2/sessions/{session_id}/snapshot", headers=HEADERS).status_code == 200
                healthy = store.snapshot(healthy_id).model_dump(mode="json")
                assert healthy["session"]["status"] == "READY"
                assert control(client, healthy, "START").json()["status"] == "APPLIED"
                legacy = store.snapshot("non-live-session").model_dump(mode="json")
                assert legacy["session"]["status"] == "READY"
                assert control(client, legacy, "START").json()["status"] == "QUEUED"
                assert create(client).status_code == 201
    finally:
        app.dependency_overrides.clear()
        auth_service.set_auth_service(None)
        get_settings.cache_clear()


def test_failed_tick_commit_does_not_publish_or_install_staged_state(api, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    client, store = api
    session_id = create(client).json()["session_id"]
    snapshot = store.snapshot(session_id).model_dump(mode="json")
    assert control(client, snapshot, "START").json()["status"] == "APPLIED"
    service = app.state.training_services[store]
    live = service.sessions[session_id]
    client.portal.call(service.step, live)
    before = live.actor
    committed_tick = store.snapshot(session_id).tick
    def fail(*_, **__):
        raise RuntimeError("injected publication failure")
    monkeypatch.setattr(store, "commit_snapshot", fail)
    with pytest.raises(RuntimeError, match="injected publication"):
        client.portal.call(service.step, live)
    assert live.actor is before
    assert store.snapshot(session_id).tick == committed_tick


def test_failed_initial_commit_leaves_no_run_or_creation(api, monkeypatch):
    client, store = api
    def fail(*_):
        raise RuntimeError("injected checkpoint failure")
    monkeypatch.setattr(store, "_write_runtime", fail)
    with pytest.raises(RuntimeError, match="injected"):
        create(client)
    assert not store.runtimes()
    assert not app.state.training_services[store].sessions
    with store._read_connection() as connection:
        assert connection.execute("SELECT count(*) FROM sessions").fetchone()[0] == 0
        assert connection.execute("SELECT count(*) FROM session_creations").fetchone()[0] == 0


def test_restart_recovers_private_state_paused_and_creation_key(tmp_path, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    async def exercise():
        database = tmp_path / "recovery.sqlite"
        request = CreateSessionRequest(**BODY)
        with TrainingStore(database) as store:
            service = TrainingSessionService(store)
            snapshot = await service.create(request, "operator", "retry-key")
            session_id = snapshot.session_id
            command = DispatcherCommand(
                contract_version="2.0", command_id="start", idempotency_key="start", session_id=session_id,
                command_type="START", expected_epoch=1, expected_revision=0,
                target_type="SESSION", target_id=session_id, payload={},
            )
            await service.submit("operator", command)
            for _ in range(12):
                await service.step(service.sessions[session_id])
            before = store.snapshot(session_id)
            await service.close()
        with TrainingStore(database) as store:
            recovered = TrainingSessionService(store)
            await recovered.initialize()
            after = store.snapshot(session_id)
            assert after.session.status == "PAUSED" and after.session_epoch == 2
            assert after.tick == before.tick and after.vehicles == before.vehicles
            assert (await recovered.create(request, "operator", "retry-key")).session_id == session_id
            assert len(recovered.sessions) == 1
            await recovered.step(recovered.sessions[session_id])
            assert store.snapshot(session_id).tick == before.tick
            command.command_id = command.idempotency_key = "resume"
            command.command_type = "RESUME"
            command.expected_epoch = after.session_epoch
            command.expected_revision = after.session.aggregate_revision
            assert (await recovered.submit("operator", command)).status == "APPLIED"
            for _ in range(2):
                await recovered.step(recovered.sessions[session_id])
            assert store.snapshot(session_id).tick == before.tick + 2
            await recovered.close()
    asyncio.run(exercise())


def test_queued_lifecycle_retry_with_new_ids_applies_the_original_once(api, monkeypatch):
    client, store = api
    session_id = create(client).json()["session_id"]
    baseline = store.snapshot(session_id).model_dump(mode="json")
    original_apply = store.apply

    def fail(*args, **kwargs):
        raise TrainingStoreError("QUEUE_FULL", "Temporary writer overload after acceptance")

    monkeypatch.setattr(store, "apply", fail)
    assert control(client, baseline, "START", "retry-start").status_code == 503
    original = store.command_status(session_id, ACTOR, "retry-start")
    assert original.status == "QUEUED"
    monkeypatch.setattr(store, "apply", original_apply)
    with ThreadPoolExecutor(max_workers=2) as pool:
        responses = list(pool.map(lambda _: control(client, baseline, "START", "retry-start"), range(2)))
    assert [r.status_code for r in responses] == [202, 202]
    assert all(r.json()["status"] == "APPLIED" and r.json()["command_id"] == original.command_id for r in responses)
    events = store.replay(session_id, 0).events
    assert [e.event_type for e in events] == ["SESSION_RUNNING", "COMMAND_APPLIED"]


def test_failed_runtime_stays_unavailable_until_failure_is_durable(api, monkeypatch):
    client, store = api
    monkeypatch.setattr(TrainingSessionService, "FAILURE_RETRY_S", 0.01)
    monkeypatch.setattr(TrainingSessionService, "MAX_SESSIONS", 1)
    session_id = create(client).json()["session_id"]
    baseline = store.snapshot(session_id).model_dump(mode="json")
    assert control(client, baseline, "START", "start-before-failure").json()["status"] == "APPLIED"
    service = app.state.training_services[store]
    live = service.sessions[session_id]
    original_commit = store.commit_snapshot
    available = ThreadEvent()
    attempts = []

    def unavailable(snapshot, *args, **kwargs):
        attempts.append(snapshot.session.status)
        if not available.is_set():
            raise TrainingStoreError("QUEUE_FULL", "Temporary persistence outage")
        return original_commit(snapshot, *args, **kwargs)

    monkeypatch.setattr(store, "commit_snapshot", unavailable)
    try:
        client.portal.call(wait_until, lambda: "FAILED" in attempts)
        response = client.get(f"/api/v2/sessions/{session_id}/snapshot", headers=HEADERS)
        assert response.status_code == 409 and response.json()["error"]["code"] == "SESSION_RECOVERING"
        assert not live.task.done()
        assert client.post("/api/v2/sessions", headers={**HEADERS, "Idempotency-Key": "after-failure"}, json=BODY).status_code == 503
        assert store.snapshot(session_id).session.status == "RUNNING"
        # Previously committed outcomes can still be reconciled during an outage.
        assert control(client, baseline, "START", "start-before-failure").json()["status"] == "APPLIED"
        current = store.snapshot(session_id).model_dump(mode="json")
        assert control(client, current, "PAUSE", "not-admitted").json()["error"]["code"] == "SESSION_RECOVERING"
        with pytest.raises(TrainingStoreError, match="not found"):
            store.command_status(session_id, ACTOR, "not-admitted")
    finally:
        available.set()
    client.portal.call(wait_until, lambda: live.task.done())
    assert live.task.exception() is None
    result = client.get(f"/api/v2/sessions/{session_id}/snapshot", headers=HEADERS)
    assert result.status_code == 200 and result.json()["session"]["status"] == "FAILED"
    assert [e.event_type for e in store.replay(session_id, 0).events].count("SESSION_FAILED") == 1
    assert control(client, baseline, "START", "start-before-failure").json()["status"] == "APPLIED"
    assert session_id not in service.sessions
    assert client.post("/api/v2/sessions", headers={**HEADERS, "Idempotency-Key": "after-failure"}, json=BODY).status_code == 201


def test_slow_tick_io_keeps_http_responsive_and_serializes_pause(api, monkeypatch):
    client, store = api
    session_id = create(client).json()["session_id"]
    baseline = store.snapshot(session_id).model_dump(mode="json")
    original_commit, original_accept = store.commit_snapshot, store.accept
    entered, release, pause_accepted = ThreadEvent(), ThreadEvent(), ThreadEvent()
    blocked_ticks = []

    def blocked_commit(snapshot, *args, **kwargs):
        if snapshot.session_id == session_id and not entered.is_set():
            blocked_ticks.append(snapshot.tick)
            entered.set()
            if not release.wait(10):
                raise RuntimeError("Test did not release the tick publication")
        return original_commit(snapshot, *args, **kwargs)

    def observe_accept(actor_id, command, **kwargs):
        if command.command_type == "PAUSE":
            pause_accepted.set()
        return original_accept(actor_id, command, **kwargs)

    monkeypatch.setattr(store, "commit_snapshot", blocked_commit)
    monkeypatch.setattr(store, "accept", observe_accept)
    assert control(client, baseline, "START").json()["status"] == "APPLIED"
    with ThreadPoolExecutor(max_workers=2) as pool:
        try:
            assert entered.wait(5)
            # This request must finish while the tick's persistence is still blocked.
            assert pool.submit(client.get, "/api/health").result(timeout=1).status_code == 200
            current = store.snapshot(session_id).model_dump(mode="json")
            pause = pool.submit(control, client, current, "PAUSE")
            assert not pause_accepted.wait(0.1)
        finally:
            release.set()
        result = pause.result(timeout=5)
    assert result.json()["status"] == "APPLIED"
    paused = store.snapshot(session_id)
    assert paused.session.status == "PAUSED" and paused.tick >= blocked_ticks[0]
    live = app.state.training_services[store].sessions[session_id]
    assert live.actor.is_paused and live.actor.current_tick == paused.tick


def test_cancelled_tick_is_drained_before_shutdown_and_recovery(tmp_path, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)

    async def exercise():
        database = tmp_path / "cancelled.sqlite"
        with TrainingStore(database) as store:
            service = TrainingSessionService(store)
            snapshot = await service.create(CreateSessionRequest(**BODY), "operator", "create")
            command = DispatcherCommand(
                contract_version="2.0", command_id="start", idempotency_key="start", session_id=snapshot.session_id,
                command_type="START", expected_epoch=1, expected_revision=0,
                target_type="SESSION", target_id=snapshot.session_id, payload={},
            )
            await service.submit("operator", command)
            live = service.sessions[snapshot.session_id]
            await service.step(live)
            entered, release = ThreadEvent(), ThreadEvent()
            original_commit = store.commit_snapshot

            def blocked(*args, **kwargs):
                entered.set()
                if not release.wait(10):
                    raise RuntimeError("Test did not release the cancelled publication")
                return original_commit(*args, **kwargs)

            monkeypatch.setattr(store, "commit_snapshot", blocked)
            pending = asyncio.create_task(service.step(live))
            closing = None
            try:
                assert await asyncio.to_thread(entered.wait, 5)
                pending.cancel()
                await asyncio.gather(pending, return_exceptions=True)
                closing = asyncio.create_task(service.close())
                await asyncio.sleep(0.02)
                assert not closing.done()
            finally:
                release.set()
                await (closing if closing is not None else service.close())
            assert live.actor.current_tick == store.snapshot(snapshot.session_id).tick == 2
        with TrainingStore(database) as store:
            service = TrainingSessionService(store)
            try:
                await service.initialize()
                assert (await service.snapshot(snapshot.session_id)).tick == 2
            finally:
                await service.close()
    asyncio.run(exercise())


def test_command_outcome_during_staged_tick_does_not_stop_runtime(api, monkeypatch):
    client, store = api
    session_id = create(client).json()["session_id"]
    baseline = store.snapshot(session_id).model_dump(mode="json")
    entered, release = ThreadEvent(), ThreadEvent()
    original_commit = store.commit_snapshot

    def blocked(snapshot, *args, **kwargs):
        if not entered.is_set():
            entered.set()
            if not release.wait(10):
                raise RuntimeError("Test did not release the staged publication")
        return original_commit(snapshot, *args, **kwargs)

    monkeypatch.setattr(store, "commit_snapshot", blocked)
    assert control(client, baseline, "START").json()["status"] == "APPLIED"
    try:
        assert entered.wait(5)
        vehicle = store.snapshot(session_id).vehicles[0]
        denied = DispatcherCommand(
            contract_version="2.0", command_id="denied", idempotency_key="denied", session_id=session_id,
            command_type="CONTINUE_SERVICE", expected_epoch=1, expected_revision=vehicle.aggregate_revision,
            target_type="VEHICLE", target_id=vehicle.vehicle_id, payload={"option_id": "missing"},
            reason="Exercise an unavailable offer while the next tick is staged",
        )
        store.accept(ACTOR, denied, defer_preconditions=True)
        assert store.apply(denied.command_id, rejection_code="OPTION_EXPIRED").status == "REJECTED"
    finally:
        release.set()
    service = app.state.training_services[store]
    live = service.sessions[session_id]
    client.portal.call(wait_until, lambda: live.actor.current_tick >= 4)
    snapshot = client.get(f"/api/v2/sessions/{session_id}/snapshot", headers=HEADERS)
    assert snapshot.status_code == 200 and snapshot.json()["session"]["status"] == "RUNNING"
    assert not live.failed
    assert store.command_status(session_id, ACTOR, "denied").status == "REJECTED"
    assert [e.event_type for e in store.replay(session_id, 0).events] == [
        "SESSION_RUNNING", "COMMAND_APPLIED", "COMMAND_REJECTED",
    ]


def test_session_ids_are_readable_and_never_reused_after_delete(api, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    client, store = api
    first = create(client).json()["session_id"]
    assert re.fullmatch(r"fleet-loop-001-\d{1,2}[a-z]{3}-\d{4}", first)
    control(client, store.snapshot(first).model_dump(mode="json"), "ARCHIVE")
    assert client.delete(f"/api/v2/sessions/{first}", headers=HEADERS).status_code == 204
    second = client.post("/api/v2/sessions", headers={**HEADERS, "Idempotency-Key": "next"},
                         json={**BODY, "scenario_revision_id": "scenario-m2-road-3d-r1"}).json()["session_id"]
    assert second.startswith("fleet-3d-002-")


def test_readable_session_id_names_place_number_date_and_time():
    when = datetime(2026, 11, 10, 13, 40)
    assert readable_session_id("scenario-m2-integrated-r1", 7, when) == "toa-payoh-007-10nov-1340"
    assert readable_session_id("unknown", 1234, datetime(2026, 1, 5, 9, 5)) == "session-1234-5jan-0905"
