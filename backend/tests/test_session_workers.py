"""Live sessions in session worker processes (session_gateway, session_worker): the same sessions as in
one process, placed and admitted by load, recovered when a worker is lost, and no process left behind."""

import asyncio
from contextlib import asynccontextmanager, closing
import json
import multiprocessing
import sqlite3
import time
from uuid import uuid4

from fastapi.testclient import TestClient
import pytest

from contracts.v2.models import DispatcherCommand
from contracts.v2.route_edit import RouteApprovalRequest, RouteEditRequest
from contracts.v2.sessions import CreateSessionRequest
from src.api.routes.training_v2 import get_actor_id, get_training_store
from src.app import app
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.services.integrated_actor import SCENARIO_SELECTOR, IntegratedSessionActor
from src.services.session_gateway import GatewaySessionService, configured_workers, create_session_service
from src.services.training_sessions import TrainingSessionService, checkpoint


async def manual_clock(self, session):
    await asyncio.Event().wait()


@asynccontextmanager
async def gateway(path, *, workers=1, manual_clock=False):
    with TrainingStore(path) as store:
        service = GatewaySessionService(store, workers=workers, manual_clock=manual_clock)
        processes = []
        try:
            await service.initialize()
            processes = [handle.process for handle in service.workers]
            yield store, service
        finally:
            processes += [handle.process for handle in service.workers if handle is not None]
            await service.close()
        assert not any(process.is_alive() for process in processes)


def command(store, session_id, kind, key=None):
    snapshot = store.snapshot(session_id)
    key = key or uuid4().hex
    return DispatcherCommand(contract_version="2.0", command_id=key, idempotency_key=key, session_id=session_id,
                             command_type=kind, expected_epoch=snapshot.session_epoch,
                             expected_revision=snapshot.session.aggregate_revision, target_type="SESSION",
                             target_id=session_id, payload={}, reason="Worker test")


async def until(condition, timeout_s, message):
    deadline = time.monotonic() + timeout_s
    while not condition():
        if time.monotonic() > deadline:
            raise AssertionError(message)
        await asyncio.sleep(0.05)


def test_worker_count_setting():
    assert configured_workers("0") == 0
    assert configured_workers("3") == 3
    assert 1 <= configured_workers("auto") <= 8
    assert configured_workers("") == configured_workers("auto")
    for value in ("-1", "many", "65"):
        with pytest.raises(ValueError):
            configured_workers(value)


def test_zero_workers_keeps_sessions_in_this_process(tmp_path):
    with TrainingStore(tmp_path / "s.sqlite") as store:
        assert type(create_session_service(store, workers=0)) is TrainingSessionService
        assert type(create_session_service(store, workers=2)) is GatewaySessionService


# Random in every run (IDs) or read from the wall clock: the rest of a committed row is the simulation's
VARYING = {"snapshot_id", "event_id", "result_event_id", "preview_id", "detected_at_utc", "wall_response_time_s"}


def _normalised(raw):
    """A committed row's JSON without what differs between runs of the same inputs (VARYING)."""
    def walk(value):
        if isinstance(value, dict):
            return {key: None if key in VARYING else walk(item) for key, item in value.items()}
        if isinstance(value, list):
            return [walk(item) for item in value]
        return value
    return walk(json.loads(raw))


def _history(path, session_id):
    with closing(sqlite3.connect(path)) as connection:
        checkpoints = [(sequence, tick, _normalised(raw)) for sequence, tick, raw in connection.execute(
            "SELECT event_sequence, tick, snapshot_json FROM checkpoints WHERE session_id=? ORDER BY rowid",
            (session_id,))]
        events = [(sequence, _normalised(raw)) for sequence, raw in connection.execute(
            "SELECT event_sequence, event_json FROM events WHERE session_id=? ORDER BY event_sequence", (session_id,))]
        runtime = connection.execute("SELECT checkpoint_json FROM session_runtimes WHERE session_id=?",
                                     (session_id,)).fetchone()[0]
        traffic = connection.execute("SELECT traffic_json FROM session_traffic WHERE session_id=?",
                                     (session_id,)).fetchone()
    return checkpoints, events, _normalised(runtime), traffic and _normalised(traffic[0])


async def _exercise(path, workers):
    """The same seed and inputs as test_training_integration's replay test: route approval, start, an
    incident decision, 480 ticks; through the in-process runtime (workers 0) or a worker process."""
    actor = IntegratedSessionActor(1000, session_id="deterministic-integration")
    with TrainingStore(path) as store:
        store.create_session(actor.generate_snapshot(), runtime_state=checkpoint(actor))
    async def run(store, service, step):
        draft = actor.definition.routes.preview(store.snapshot(actor.session_id), RouteEditRequest(
            route_id="route-peer-a", expected_route_revision=1, draft_revision=1))
        result = await service.approve_route("operator", actor.session_id, RouteApprovalRequest(
            preview_id=draft.preview_id, preview_revision=draft.preview_revision, command_id="route",
            idempotency_key="route", target_vehicle_ids=["shuttle-01"], reason="Deterministic pre-trip bypass"))
        assert result.status == "APPLIED"
        assert (await service.submit("operator", command(store, actor.session_id, "START", "start"))).status == "APPLIED"
        await step(240)
        snapshot = store.snapshot(actor.session_id)
        incident = snapshot.incidents[0]
        option = next(c for c in snapshot.candidates if c.action == "KEEP_ROUTE")
        result = await service.submit("operator", DispatcherCommand(
            contract_version="2.0", command_id="wait", idempotency_key="wait", session_id=actor.session_id,
            command_type="APPROVE_INCIDENT_OPTION", expected_epoch=snapshot.session_epoch,
            expected_revision=incident.aggregate_revision, target_type="INCIDENT", target_id=incident.incident_id,
            payload={"candidate_id": option.candidate_id}, reason="Deterministic wait"))
        assert result.status == "APPLIED"
        await step(240)
    if workers:
        async with gateway(path, workers=workers, manual_clock=True) as (store, service):
            async def step(ticks):
                await service.step(actor.session_id, ticks)
            await run(store, service, step)
    else:
        with TrainingStore(path) as store:
            service = TrainingSessionService(store)
            try:
                await service.initialize()
                async def step(ticks):
                    for _ in range(ticks):
                        await service.step(service.sessions[actor.session_id])
                await run(store, service, step)
            finally:
                await service.close()
    return _history(path, actor.session_id)


@pytest.mark.parametrize("traffic", [False, pytest.param(True, marks=pytest.mark.traffic)])
def test_a_worker_commits_exactly_what_the_in_process_runtime_commits(tmp_path, monkeypatch, traffic):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    monkeypatch.setattr(IntegratedSessionActor, "TRAFFIC", traffic)
    in_process = asyncio.run(_exercise(tmp_path / "in-process.sqlite", 0))
    in_worker = asyncio.run(_exercise(tmp_path / "worker.sqlite", 2))
    checkpoints, events, runtime, saved_traffic = in_worker
    assert len(checkpoints) > 200 and checkpoints[-1][1] == 480
    assert (saved_traffic is not None) == traffic
    assert checkpoints == in_process[0]
    assert events == in_process[1]
    assert runtime == in_process[2]
    assert saved_traffic == in_process[3]


@pytest.fixture
def http(tmp_path, monkeypatch):
    monkeypatch.setenv("F9_SESSION_WORKERS", "1")
    previous = dict(app.dependency_overrides)
    try:
        with TrainingStore(tmp_path / "http.sqlite") as store:
            app.dependency_overrides[get_training_store] = lambda: store
            app.dependency_overrides[get_actor_id] = lambda: "worker-operator"
            with TestClient(app) as client:
                service = app.state.training_services[store]
                assert isinstance(service, GatewaySessionService)
                processes = [handle.process for handle in service.workers]
                yield client, store, service
            assert not any(process.is_alive() for process in processes)
    finally:
        app.dependency_overrides.clear()
        app.dependency_overrides.update(previous)


def test_a_session_runs_its_whole_lifecycle_through_a_worker(http):
    client, store, service = http
    created = client.post("/api/v2/sessions", headers={"Idempotency-Key": "worker-lifecycle"},
                          json={"scenario_revision_id": SCENARIO_SELECTOR, "seed": 1000})
    assert created.status_code == 201, created.text
    session_id = created.json()["session_id"]
    assert client.post("/api/v2/sessions", headers={"Idempotency-Key": "worker-lifecycle"},
                       json={"scenario_revision_id": SCENARIO_SELECTOR, "seed": 1000}).json()["session_id"] == session_id
    listed = client.get("/api/v2/sessions").json()
    assert listed["live_count"] == 1 and listed["sessions"][0]["session_id"] == session_id

    draft = client.post(f"/api/v2/sessions/{session_id}/route-previews",
                        json={"route_id": "route-peer-a", "expected_route_revision": 1, "draft_revision": 1})
    assert draft.status_code == 200, draft.text
    approved = client.post(f"/api/v2/sessions/{session_id}/route-approvals", json={
        "preview_id": draft.json()["preview_id"], "preview_revision": draft.json()["preview_revision"],
        "command_id": uuid4().hex, "idempotency_key": uuid4().hex, "target_vehicle_ids": ["shuttle-01"],
        "reason": "Approved through a worker"})
    assert approved.status_code == 200 and approved.json()["status"] == "APPLIED", approved.text

    def control(kind):
        response = client.post(f"/api/v2/sessions/{session_id}/commands",
                               json=command(store, session_id, kind).model_dump(mode="json"))
        assert response.status_code == 202, response.text
        assert response.json()["status"] == "APPLIED", response.json()

    def tick():
        return client.get(f"/api/v2/sessions/{session_id}/snapshot").json()["tick"]

    control("START")
    deadline = time.monotonic() + 10
    while tick() < 20:
        assert time.monotonic() < deadline, "The session did not tick in its worker"
        time.sleep(0.05)
    signals = client.get(f"/api/v2/sessions/{session_id}/signals/state")
    assert signals.status_code == 200 and signals.json()["session_id"] == session_id
    control("PAUSE")
    paused = tick()
    time.sleep(0.3)
    assert tick() == paused
    control("RESUME")
    control("ARCHIVE")
    assert not service.owns(session_id)
    assert client.get("/api/v2/sessions").json()["live_count"] == 0
    assert client.get(f"/api/v2/sessions/{session_id}/snapshot").json()["session"]["status"] == "ARCHIVED"
    assert client.delete(f"/api/v2/sessions/{session_id}").status_code == 204


@pytest.mark.traffic
def test_traffic_long_poll_wakes_once_per_frame(tmp_path, monkeypatch):
    monkeypatch.setattr(IntegratedSessionActor, "TRAFFIC", True)

    async def exercise():
        async with gateway(tmp_path / "traffic.sqlite") as (store, service):
            created = await service.create(CreateSessionRequest(scenario_revision_id=SCENARIO_SELECTOR, seed=1000),
                                           "operator", "traffic")
            session_id = created.session_id
            assert service.traffic_sessions() == 1
            # Joined before it starts: the frame of where the traffic stands
            assert len(service.traffic(session_id, -1)) == 1
            await service.submit("operator", command(store, session_id, "START"))
            after = json.loads(service.traffic(session_id, -1)[-1])["tick"]
            ticks, waits = [], []
            while len(ticks) < 12:
                started = time.perf_counter()
                frames = await service.wait_for_traffic(session_id, after, 2.0)
                waits.append(time.perf_counter() - started)
                assert frames, "A long poll ended without a frame while the session ran"
                ticks += [json.loads(frame)["tick"] for frame in frames]
                after = ticks[-1]
            # One frame a traffic step, every second tick, none missed or repeated
            assert all(b - a == 2 for a, b in zip(ticks, ticks[1:])), ticks
            # Each reply comes as its frame is made: a tenth of a second apart, not a polling interval
            assert max(waits[1:]) < 0.5, waits
    asyncio.run(exercise())


def test_killing_a_worker_recovers_its_sessions_and_leaves_the_others_running(tmp_path):
    async def exercise():
        async with gateway(tmp_path / "kill.sqlite", workers=2) as (store, service):
            ids = []
            for index in range(2):
                created = await service.create(
                    CreateSessionRequest(scenario_revision_id=SCENARIO_SELECTOR, seed=1000 + index),
                    "operator", f"kill-{index}")
                ids.append(created.session_id)
            lost, kept = ids
            assert service.sessions[lost].worker is not service.sessions[kept].worker
            start = command(store, lost, "START", "start-lost")
            assert (await service.submit("operator", start)).status == "APPLIED"
            assert (await service.submit("operator", command(store, kept, "START"))).status == "APPLIED"
            await until(lambda: store.snapshot(lost).tick >= 20 and store.snapshot(kept).tick >= 20, 10,
                        "The sessions did not run")
            epoch, kept_epoch = store.snapshot(lost).session_epoch, store.snapshot(kept).session_epoch
            kept_worker = service.sessions[kept].worker
            before = service.sessions[lost]
            killed = before.worker
            killed.process.kill()
            started = time.monotonic()
            await until(lambda: service.sessions.get(lost) not in (None, before)
                        and not service.sessions[lost].recovering, 5, "The lost worker's session was not recovered")
            assert time.monotonic() - started < 5
            # Until then it answered SESSION_RECOVERING
            assert before.recovering and service._unavailable(before)
            # Back from its last committed checkpoint, in a new epoch, paused, on the worker still running
            recovered = store.snapshot(lost)
            assert recovered.session_epoch == epoch + 1 and recovered.session.status == "PAUSED"
            assert service.sessions[lost].worker is kept_worker
            # The other worker's session never stopped
            assert service.sessions[kept].worker is kept_worker and not service.sessions[kept].recovering
            kept_tick = store.snapshot(kept).tick
            await until(lambda: store.snapshot(kept).tick > kept_tick, 2, "The other session stopped")
            assert store.snapshot(kept).session_epoch == kept_epoch
            # The command acknowledged before the loss keeps its one outcome
            replay = await service.submit("operator", start)
            assert replay.status == "APPLIED" and replay.command_id == "start-lost"
            with closing(sqlite3.connect(store.path)) as connection:
                applied = connection.execute(
                    "SELECT COUNT(*) FROM events WHERE session_id=?"
                    " AND json_extract(event_json, '$.event_type')='COMMAND_APPLIED'"
                    " AND json_extract(event_json, '$.entity_id')='start-lost'", (lost,)).fetchone()[0]
            assert applied == 1
            # And the session goes on
            assert (await service.submit("operator", command(store, lost, "RESUME"))).status == "APPLIED"
            resumed = store.snapshot(lost).tick
            await until(lambda: store.snapshot(lost).tick > resumed, 5, "The recovered session did not resume")
            # A replacement takes the lost worker's place
            await until(lambda: service.workers[killed.index] is not None
                        and service.workers[killed.index] is not killed, 60, "No replacement worker started")
            assert not killed.process.is_alive()
    asyncio.run(exercise())


def test_admission_refuses_when_every_worker_is_at_its_budget(tmp_path):
    async def exercise():
        async with gateway(tmp_path / "admission.sqlite", workers=2) as (store, service):
            # Each session costs more than this budget: an empty worker still takes one
            service.TICK_BUDGET_MS = 1.0
            placed = []
            for index in range(2):
                created = await service.create(
                    CreateSessionRequest(scenario_revision_id=SCENARIO_SELECTOR, seed=1000 + index),
                    "operator", f"admit-{index}")
                placed.append(service.sessions[created.session_id].worker)
            assert placed[0] is not placed[1]
            with pytest.raises(TrainingStoreError) as refused:
                await service.create(CreateSessionRequest(scenario_revision_id=SCENARIO_SELECTOR, seed=2000),
                                     "operator", "admit-refused")
            assert refused.value.code == "QUEUE_FULL"
            # Within the budget, the least loaded worker takes it
            service.TICK_BUDGET_MS = 30.0
            created = await service.create(CreateSessionRequest(scenario_revision_id=SCENARIO_SELECTOR, seed=2000),
                                           "operator", "admit-later")
            assert service.sessions[created.session_id].worker in placed
    asyncio.run(exercise())


def test_sessions_left_running_are_restored_onto_workers_at_startup(tmp_path):
    path = tmp_path / "restart.sqlite"

    async def first():
        async with gateway(path, workers=2) as (store, service):
            for index in range(3):
                created = await service.create(
                    CreateSessionRequest(scenario_revision_id=SCENARIO_SELECTOR, seed=1000 + index),
                    "operator", f"restart-{index}")
                await service.submit("operator", command(store, created.session_id, "START"))
            await until(lambda: all(tick and tick > 10 for tick in service.live_ticks().values()), 10,
                        "The sessions did not run")
            return sorted(service.sessions)

    async def second():
        async with gateway(path, workers=2) as (store, service):
            assert sorted(service.sessions) == ids
            # Spread over both workers, paused as recovery leaves them
            assert len({session.worker.index for session in service.sessions.values()}) == 2
            assert all(store.snapshot(session_id).session.status == "PAUSED" for session_id in ids)

    ids = asyncio.run(first())
    asyncio.run(second())


def test_shutdown_leaves_no_worker_processes(tmp_path):
    async def exercise():
        async with gateway(tmp_path / "shutdown.sqlite", workers=2) as (store, service):
            created = await service.create(CreateSessionRequest(scenario_revision_id=SCENARIO_SELECTOR, seed=1000),
                                           "operator", "shutdown")
            await service.submit("operator", command(store, created.session_id, "START"))
            await asyncio.sleep(0.5)
            processes = [handle.process for handle in service.workers]
        return processes
    processes = asyncio.run(exercise())
    assert processes and not any(process.is_alive() for process in processes)
    assert not [child for child in multiprocessing.active_children() if child.name.startswith("f9-session-worker")]
