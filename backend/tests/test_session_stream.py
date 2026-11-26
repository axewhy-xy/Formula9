"""The live session stream (WS /api/v2/sessions/{session_id}/stream, session_stream.py): each newer committed
snapshot and every traffic frame pushed as made, in process and through a session worker, and the
stream ending with a reason the client can act on."""

import asyncio
import json
import threading
import time
from uuid import uuid4

from fastapi.testclient import TestClient
from pydantic import TypeAdapter
import pytest
from starlette.websockets import WebSocketDisconnect

from contracts.v2.models import DispatcherCommand
from contracts.v2.sessions import CreateSessionRequest
from contracts.v2.stream import StreamMessage
from src.api.routes.training_stream import stream_actor_id
from src.api.routes.training_v2 import get_actor_id, get_training_store
from src.app import app
from src.persistence.training_store import TrainingStore
from src.services.integrated_actor import SCENARIO_SELECTOR
from src.services.session_gateway import GatewaySessionService
from src.services.session_stream import HEARTBEAT_S, SnapshotFeed
from src.services.training_sessions import TrainingSessionService

MESSAGE = TypeAdapter(StreamMessage)


@pytest.fixture
def http(tmp_path):
    previous = dict(app.dependency_overrides)
    try:
        with TrainingStore(tmp_path / "stream.sqlite") as store:
            app.dependency_overrides[get_training_store] = lambda: store
            app.dependency_overrides[get_actor_id] = lambda: "stream-operator"
            app.dependency_overrides[stream_actor_id] = lambda: "stream-operator"
            with TestClient(app) as client:
                yield client, store, app.state.training_services[store]
    finally:
        app.dependency_overrides.clear()
        app.dependency_overrides.update(previous)


def create(client, key="stream-attempt"):
    created = client.post("/api/v2/sessions", headers={"Idempotency-Key": key},
                          json={"scenario_revision_id": SCENARIO_SELECTOR, "seed": 1000})
    assert created.status_code == 201, created.text
    return created.json()["session_id"]


def control(client, session_id, kind):
    snapshot = client.get(f"/api/v2/sessions/{session_id}/snapshot").json()
    key = uuid4().hex
    command = DispatcherCommand(contract_version="2.0", command_id=key, idempotency_key=key, session_id=session_id,
                                command_type=kind, expected_epoch=snapshot["session_epoch"],
                                expected_revision=snapshot["session"]["aggregate_revision"], target_type="SESSION",
                                target_id=session_id, payload={}, reason="Stream test")
    reply = client.post(f"/api/v2/sessions/{session_id}/commands", json=command.model_dump(mode="json"))
    assert reply.status_code == 202, reply.text


def receive(socket):
    """The next message, checked against the stream contract, with when it arrived."""
    text = socket.receive_text()
    MESSAGE.validate_json(text)
    return json.loads(text), time.monotonic()


def collect(socket, until, timeout_s=10.0):
    """Messages until until(messages) holds, failing after timeout_s."""
    messages = []
    deadline = time.monotonic() + timeout_s
    while not until(messages):
        assert time.monotonic() < deadline, [m["type"] for m, _ in messages]
        messages.append(receive(socket))
    return messages


def snapshots(messages):
    return [(m["snapshot"], at) for m, at in messages if m["type"] == "snapshot"]


def assert_newer_each_time(pushed):
    """Each snapshot newer than the one before: never older, never the same one twice. START is applied
    at the tick the session stood at, so a newer snapshot can share a tick with more events."""
    boundary = [(s["tick"], s["event_sequence"]) for s in pushed]
    assert all(b >= a for a, b in zip(boundary, boundary[1:])), boundary
    assert len({s["snapshot_id"] for s in pushed}) == len(pushed), boundary
    assert boundary[-1][0] > 0, boundary


def frame_ticks(messages):
    return [frame["tick"] for m, _ in messages if m["type"] == "traffic" for frame in m["frames"]]


@pytest.mark.traffic
def test_a_stream_pushes_each_newer_snapshot_and_every_traffic_frame(http):
    client, store, service = http
    assert isinstance(service, TrainingSessionService)
    session_id = create(client)
    with client.websocket_connect(f"/api/v2/sessions/{session_id}/stream?traffic=true&snapshot_interval_ms=100") as socket:
        # At once: the committed snapshot, then where the traffic stands, though nothing runs yet
        first, _ = receive(socket)
        assert first["type"] == "snapshot" and first["session_id"] == session_id
        assert first["snapshot"]["tick"] == 0 and first["snapshot"]["session"]["status"] == "READY"
        opening, _ = receive(socket)
        assert opening["type"] == "traffic" and [f["tick"] for f in opening["frames"]] == [0]

        control(client, session_id, "START")
        messages = collect(socket, lambda m: len(snapshots(m)) >= 12)
        pushed = snapshots(messages)
        assert_newer_each_time([s for s, _ in pushed])
        assert pushed[-1][0]["session"]["status"] == "RUNNING"
        # No closer together than asked (a little slack for the event loop), and far sooner than a 1 s poll
        gaps = [b - a for (_, a), (_, b) in zip(pushed, pushed[1:])]
        # (arrival times here carry the test client's thread hand-off and the clock's granularity)
        assert min(gaps) > 0.05 and sum(gaps) / len(gaps) > 0.09 and sorted(gaps)[len(gaps) // 2] < 0.5, gaps
        # Every traffic step's frame, once, in order: none missed, none repeated
        traffic = frame_ticks(messages)
        assert traffic and all(b - a == 2 for a, b in zip(traffic, traffic[1:])), traffic

        control(client, session_id, "PAUSE")
        collect(socket, lambda m: any(s["session"]["status"] == "PAUSED" for s, _ in snapshots(m)))
        # Ended sessions are no longer kept in memory; the stream still sends where it ended
        control(client, session_id, "ARCHIVE")
        collect(socket, lambda m: any(s["session"]["status"] == "ARCHIVED" for s, _ in snapshots(m)))
        # Deleted: the stream says so and closes
        assert client.delete(f"/api/v2/sessions/{session_id}").status_code == 204
        ended = collect(socket, lambda m: m and m[-1][0]["type"] == "error")
        assert ended[-1][0]["error"]["code"] == "INVALID_REFERENCE" and not ended[-1][0]["error"]["retryable"]
        with pytest.raises(WebSocketDisconnect) as closed:
            socket.receive_text()
        assert closed.value.code == 4404


def test_a_quiet_session_sends_heartbeats(http, monkeypatch):
    client, store, service = http
    monkeypatch.setattr("src.services.session_stream.HEARTBEAT_S", 0.2)
    session_id = create(client)
    with client.websocket_connect(f"/api/v2/sessions/{session_id}/stream") as socket:
        first, _ = receive(socket)
        assert first["type"] == "snapshot"
        # A READY session commits nothing: the stream says it is there, and sends nothing it sent already
        quiet = collect(socket, lambda m: len(m) >= 2, timeout_s=5)
        assert [m["type"] for m, _ in quiet] == ["heartbeat", "heartbeat"]
    assert 1 <= HEARTBEAT_S < 5  # Idle heartbeats arrive before the client's action freshness expires.


@pytest.mark.traffic
def test_traffic_comes_only_while_the_client_wants_it(http):
    client, store, service = http
    session_id = create(client)
    with client.websocket_connect(f"/api/v2/sessions/{session_id}/stream?snapshot_interval_ms=100") as socket:
        control(client, session_id, "START")
        quiet = collect(socket, lambda m: len(snapshots(m)) >= 6)
        assert frame_ticks(quiet) == []
        # A message the backend does not know is refused, and the stream goes on
        socket.send_text(json.dumps({"type": "subscribe"}))
        refused = collect(socket, lambda m: m and m[-1][0]["type"] == "error")
        assert refused[-1][0]["error"]["code"] == "INVALID_PAYLOAD"
        socket.send_text(json.dumps({"type": "traffic", "enabled": True}))
        wanted = collect(socket, lambda m: len(frame_ticks(m)) >= 15)
        assert any(m["type"] == "snapshot" for m, _ in wanted)
        # Turned off, at most the batch already on its way comes
        socket.send_text(json.dumps({"type": "traffic", "enabled": False}))
        later = collect(socket, lambda m: len(snapshots(m)) >= 6)
        assert len(frame_ticks(later)) <= 10


def test_a_stream_refuses_an_unknown_session(http):
    client, store, service = http
    with client.websocket_connect("/api/v2/sessions/no-such-session/stream") as socket:
        message, _ = receive(socket)
        assert message["type"] == "error" and message["error"]["code"] == "INVALID_REFERENCE"
        with pytest.raises(WebSocketDisconnect) as closed:
            socket.receive_text()
        assert closed.value.code == 4404


def test_a_stream_needs_a_login(http):
    client, store, service = http
    # No login on the connection: the authentication middleware leaves it anonymous
    del app.dependency_overrides[stream_actor_id]
    session_id = create(client)
    with client.websocket_connect(f"/api/v2/sessions/{session_id}/stream") as socket:
        message, _ = receive(socket)
        assert message["type"] == "error" and message["error"]["code"] == "ROLE_DENIED"
        with pytest.raises(WebSocketDisconnect) as closed:
            socket.receive_text()
        assert closed.value.code == 4403


def test_a_stream_ends_once_its_login_is_gone(http, monkeypatch):
    from src.auth import service as auth_service
    from src.auth.service import AccountsUnavailable, AuthService
    from src.auth.store import MemoryAccountStore
    from src.services import session_stream

    client, store, service = http
    accounts = AuthService(MemoryAccountStore())
    monkeypatch.setattr(auth_service, "_service", accounts)
    monkeypatch.setattr(session_stream, "LOGIN_CHECK_S", 0.2)
    token = accounts.sign_up("stream-operator", "a long password")["token"]
    # The real login on the connection, as the client sends it
    del app.dependency_overrides[stream_actor_id]
    session_id = create(client)
    headers = {"Authorization": f"Bearer {token}"}
    with client.websocket_connect(f"/api/v2/sessions/{session_id}/stream", headers=headers) as socket:
        first, _ = receive(socket)
        assert first["type"] == "snapshot"
        # The account database unreachable is not a login gone: the stream goes on
        lookups, down = [], [True]
        user_for_token = accounts.user_for_token

        def lookup(token):
            lookups.append(token)
            if down[0]:
                raise AccountsUnavailable("down")
            return user_for_token(token)
        monkeypatch.setattr(accounts, "user_for_token", lookup)
        deadline = time.monotonic() + 5
        while len(lookups) < 2:
            assert time.monotonic() < deadline, "The login was not checked again"
            time.sleep(0.05)
        down[0] = False
        accounts.log_out(token)
        messages = collect(socket, lambda m: m and m[-1][0]["type"] == "error", timeout_s=5)
        assert messages[-1][0]["error"]["code"] == "ROLE_DENIED"
        with pytest.raises(WebSocketDisconnect) as closed:
            while True:
                socket.receive_text()
        assert closed.value.code == 4403


def test_the_store_wakes_a_feed_from_its_writer_thread(tmp_path):
    async def exercise():
        with TrainingStore(tmp_path / "feed.sqlite") as store:
            heard = []
            store.add_publish_listener(heard.append)
            service = TrainingSessionService(store)
            try:
                feed = service.snapshot_feed()
                assert service.snapshot_feed() is feed
                created = await service.create(CreateSessionRequest(scenario_revision_id=SCENARIO_SELECTOR, seed=1000),
                                               "operator", "feed")
                session_id = created.session_id
                assert heard == [session_id] and threading.current_thread() is threading.main_thread()
                with feed.subscribe(session_id):
                    signal = feed.signal(session_id)
                    # A publication on the writer thread sets the signal on this loop
                    store._submit(lambda _: store._publish(session_id, None))
                    await asyncio.wait_for(signal.wait(), 2)
                    assert feed.signal(session_id) is not signal
                    # Another session's commits do not wake this one
                    other = feed.signal(session_id)
                    feed._published("another-session")
                    await asyncio.sleep(0.05)
                    assert not other.is_set()
                assert feed.streams == 0
            finally:
                await service.close()
            # Closing the service ends the feed: a stream waiting wakes, and the store forgets it
            assert feed.closed and feed.signal(session_id).is_set()
            assert feed._published not in store._publish_listeners
            store.remove_publish_listener(heard.append)
    asyncio.run(exercise())


def test_a_feed_wakes_every_waiting_stream_when_it_closes():
    class Store:
        listeners = []

        def add_publish_listener(self, listener):
            self.listeners.append(listener)

        def remove_publish_listener(self, listener):
            self.listeners.remove(listener)

    async def exercise():
        store = Store()
        feed = SnapshotFeed(store, asyncio.get_running_loop())
        with feed.subscribe("a"), feed.subscribe("b"):
            waiting = [feed.signal("a"), feed.signal("b")]
            feed.close()
            assert all(signal.is_set() for signal in waiting) and store.listeners == []
    asyncio.run(exercise())


@pytest.mark.traffic
def test_a_stream_through_the_gateway_pushes_a_worker_sessions_snapshots_and_frames(tmp_path, monkeypatch):
    monkeypatch.setenv("F9_SESSION_WORKERS", "1")
    previous = dict(app.dependency_overrides)
    try:
        with TrainingStore(tmp_path / "gateway.sqlite") as store:
            app.dependency_overrides[get_training_store] = lambda: store
            app.dependency_overrides[get_actor_id] = lambda: "stream-operator"
            app.dependency_overrides[stream_actor_id] = lambda: "stream-operator"
            with TestClient(app) as client:
                service = app.state.training_services[store]
                assert isinstance(service, GatewaySessionService)
                processes = [handle.process for handle in service.workers]
                session_id = create(client, "gateway-stream")
                with client.websocket_connect(f"/api/v2/sessions/{session_id}/stream?traffic=true&snapshot_interval_ms=100") as socket:
                    control(client, session_id, "START")
                    messages = collect(socket, lambda m: len(snapshots(m)) >= 8 and len(frame_ticks(m)) >= 8, timeout_s=20)
                    assert_newer_each_time([s for s, _ in snapshots(messages)])
                    traffic = frame_ticks(messages)
                    assert all(b > a for a, b in zip(traffic, traffic[1:])), traffic
                    assert all(b - a == 2 for a, b in zip(traffic[1:], traffic[2:])), traffic
            assert not any(process.is_alive() for process in processes)
    finally:
        app.dependency_overrides.clear()
        app.dependency_overrides.update(previous)
