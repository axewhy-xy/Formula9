"""
Live backend integration tests for the BE-M2-02 through BE-M2-05 training slice.

Author: Nur Syazliyarni Binte Zazali
Date Created: 28/10/2026
Last Modified: 28/10/2026

Description:
    Exercises real HTTP admission, route approval, incident choices/history,
    depot travel and charging on one authoritative actor and SQLite journal.
    Checks peer agreement, idempotency/conflicts, atomic failure behavior,
    deterministic motion and recovery of private workflow execution.
"""

import asyncio
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from copy import deepcopy
import hashlib
import json
import math
import sqlite3
from uuid import uuid4

from fastapi.testclient import TestClient
import pytest

from contracts.v2.models import DispatcherCommand
from src.api.routes.training_v2 import get_actor_id, get_training_store
from src.app import app
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.routing.build_singapore_fleet_fixture import low_battery_kwh
from src.services.integrated_actor import IntegratedSessionActor, SCENARIO_SELECTOR, get_workflow_definition
from src.schemas.shuttle_data import ShuttleState
from src.services.training_sessions import TrainingSessionService, checkpoint
from src.services.training_workflow import publish_workflow


def bay(charger_id="charger-1"):
    x, y = get_workflow_definition().data["bay_positions_m"][charger_id]
    return {"x_m": float(x), "y_m": float(y), "z_m": 0.}


def parking(index):
    x, y = get_workflow_definition().parking_positions[index]
    return {"x_m": float(x), "y_m": float(y), "z_m": 0.}


def on_branch(position, branch):
    """On the fixture's named road chain (e.g. bypass-b), and past the loop entrance they share."""
    definition = get_workflow_definition()
    start = definition.data["nodes"][str(definition.data["route_edit"]["start_node_id"])]
    away = (position["x_m"], position["y_m"]) != tuple(start)
    return away and definition.lies_on(position["x_m"], position["y_m"], branch)


def only_on(branch):
    """A node no other road chain uses, to ask the route editor for that branch."""
    edges = get_workflow_definition().data["edges"]
    def nodes(prefix):
        return {n for identifier, pair in edges.items() if identifier.startswith(prefix + ":") for n in pair}
    others = set().union(*(nodes(p) for p in ("main", "bypass-b", "bypass-c", "return") if p != branch))
    return min(nodes(branch) - others)


def make_low(index):
    """Low enough for a charge offer, still able to reach the real depot."""
    return lambda actor: setattr(actor.shuttles[index], "battery",
                                 low_battery_kwh(actor, index)*100/actor.BATTERY_CAPACITY_KWH)


async def manual_clock(self, session):
    await asyncio.Event().wait()


@pytest.fixture
def runtime(tmp_path, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    previous = dict(app.dependency_overrides)
    try:
        with TrainingStore(tmp_path/"integrated.sqlite") as store:
            app.dependency_overrides[get_training_store] = lambda: store
            app.dependency_overrides[get_actor_id] = lambda: "integration-operator"
            with TestClient(app) as client:
                created = client.post("/api/v2/sessions", headers={"Idempotency-Key": "integrated-attempt"},
                    json={"scenario_revision_id": SCENARIO_SELECTOR, "seed": 1000})
                assert created.status_code == 201
                session_id = created.json()["session_id"]
                yield client, store, app.state.training_services[store], session_id
    finally:
        app.dependency_overrides.clear()
        app.dependency_overrides.update(previous)


def read(runtime):
    client, store, service, session_id = runtime
    response = client.get(f"/api/v2/sessions/{session_id}/snapshot")
    assert response.status_code == 200
    return response.json()


def send(runtime, kind, *, snapshot=None, target_type="SESSION", target_id=None,
         revision=None, payload=None, key=None, command_id=None, expected=202):
    client, store, service, session_id = runtime
    snapshot = snapshot or read(runtime)
    body = {"contract_version": "2.0", "command_id": command_id or uuid4().hex,
        "idempotency_key": key or uuid4().hex, "session_id": session_id, "command_type": kind,
        "expected_epoch": snapshot["session_epoch"], "expected_revision": revision if revision is not None else snapshot["session"]["aggregate_revision"],
        "target_type": target_type, "target_id": target_id or session_id, "payload": payload or {},
        "reason": "Authored integration exercise decision"}
    response = client.post(f"/api/v2/sessions/{session_id}/commands", json=body)
    assert response.status_code == expected, response.text
    return response.json()


def preview(runtime, **changes):
    client, store, service, session_id = runtime
    body = {"route_id": "route-m2-detour", "expected_route_revision": 1, "draft_revision": 1, **changes}
    response = client.post(f"/api/v2/sessions/{session_id}/route-previews", json=body)
    assert response.status_code == 200, response.text
    return response.json()


def approve(runtime, draft=None, *, key="route-approval"):
    client, store, service, session_id = runtime
    draft = draft or preview(runtime)
    response = client.post(f"/api/v2/sessions/{session_id}/route-approvals", json={
        "preview_id": draft["preview_id"], "preview_revision": draft["preview_revision"],
        "command_id": uuid4().hex, "idempotency_key": key,
        "target_vehicle_ids": ["shuttle-01"], "reason": "Avoid the permanent authored main-road closure"})
    assert response.status_code == 200, response.text
    return response.json()


def advance(runtime, steps):
    client, store, service, session_id = runtime
    async def run():
        for _ in range(steps):
            await service.step(service.sessions[session_id])
    client.portal.call(run)


def advance_until(runtime, predicate, limit=12000):
    client, store, service, session_id = runtime
    async def run():
        for _ in range(limit//2):
            if predicate(store.snapshot(session_id)):
                return
            await service.step(service.sessions[session_id])
            await service.step(service.sessions[session_id])
        raise AssertionError("Workflow did not reach its expected committed state")
    client.portal.call(run)


def incident_decision(runtime, action):
    snapshot = read(runtime)
    incident = snapshot["incidents"][0]
    option = next(c for c in snapshot["candidates"] if c["action"] == action)
    assert option["validation"] == "PASS"
    return send(runtime, "APPROVE_INCIDENT_OPTION", snapshot=snapshot, target_type="INCIDENT",
        target_id=incident["incident_id"], revision=incident["aggregate_revision"],
        payload={"candidate_id": option["candidate_id"]}, key="incident-choice")


def reserve(runtime, vehicle_id="shuttle-05", key="battery-choice"):
    snapshot = read(runtime)
    option = next(o for o in snapshot["charge_options"] if o["vehicle_id"] == vehicle_id and o["action"] == "CHARGE_NOW")
    assert option["validation"] == "PASS"
    charger = next(c for c in snapshot["chargers"] if c["charger_id"] == option["charger_id"])
    return send(runtime, "RESERVE_CHARGER", snapshot=snapshot, target_type="CHARGER",
        target_id=charger["charger_id"], revision=charger["aggregate_revision"],
        payload={"option_id": option["option_id"], "option_revision": option["revision"]}, key=key)


def test_one_live_session_runs_route_incident_depot_and_charging(runtime):
    client, store, service, session_id = runtime
    initial = read(runtime)
    assert len(initial["vehicles"]) == 5 and len(initial["routes"]) == 5 and len(initial["chargers"]) == 2
    assert send(runtime, "START")["status"] == "REJECTED"  # Closed route never silently starts.
    assert read(runtime)["session"]["status"] == "READY"
    draft = preview(runtime)
    assert draft["approvable"] and draft["directed_edge_ids"][0] == "bypass-b:0:forward"
    approved = approve(runtime, draft)
    assert approved["status"] == "APPLIED"
    assert approve(runtime, draft)["command_id"] == approved["command_id"]
    assert send(runtime, "START", key="start")["status"] == "APPLIED"
    assert reserve(runtime)["status"] == "APPLIED"
    advance(runtime, 240)
    detected = read(runtime)
    assert detected["vehicles"][0]["service_state"] == "AWAITING_GUIDANCE"
    assert detected["vehicles"][0]["position"] != initial["vehicles"][0]["position"]
    assert detected["vehicles"][1]["position"] != initial["vehicles"][1]["position"]
    incident_id = detected["incidents"][0]["incident_id"]
    history = client.get(f"/api/v2/sessions/{session_id}/incidents/{incident_id}/history").json()
    assert history["complete"] and history["source_mode"] == "HISTORICAL_SIMULATION" and history["camera"] is None
    assert incident_decision(runtime, "ESCALATE")["status"] == "APPLIED"
    advance(runtime, 240)
    continued = read(runtime)
    assert continued["incidents"][0]["state"] == "RESOLVED"
    assert on_branch(continued["vehicles"][0]["position"], "bypass-b")  # Actor follows bypass B, not the closed main road.
    assert continued["vehicles"][4]["service_state"] == "RETURNING_TO_DEPOT"
    assert continued["vehicles"][4]["position"] != initial["vehicles"][4]["position"]
    advance_until(runtime, lambda s: s.vehicles[4].service_state == "CHARGING")
    charging = read(runtime)
    assert charging["vehicles"][4]["position"] == bay()
    before_charge = charging["vehicles"][4]["battery_kwh"]
    advance_until(runtime, lambda s: s.vehicles[4].service_state == "STANDBY", limit=1600)
    finished = read(runtime)
    assert finished["vehicles"][4]["battery_kwh"] > before_charge
    assert all("shuttle-05" not in c["occupied_vehicle_ids"] for c in finished["chargers"])
    debrief = client.get(f"/api/v2/sessions/{session_id}/incidents/{incident_id}/debrief").json()
    assert debrief["state"] == "RESOLVED" and debrief["assessment"]["provisional"]
    events = []
    cursor = 0
    while True:
        page = store.replay(session_id, cursor, 100)
        events.extend(page.events)
        if not page.events:
            break
        cursor = page.events[-1].event_sequence
    names = [event.event_type for event in events]
    assert {"ROUTE_APPROVED", "INCIDENT_DECISION_RECORDED", "CHARGER_RESERVED", "DEPOT_BAY_ARRIVED", "CHARGING_STARTED", "CHARGING_FINISHED"} <= set(names)
    assert names.count("CHARGER_RESERVED") == names.count("CHARGING_FINISHED") == 1
    assert [e.event_sequence for e in events] == list(range(1, len(events)+1))
    assert len(service.sessions) == 1
    send(runtime, "PAUSE")
    with ThreadPoolExecutor(max_workers=2) as pool:
        peers = list(pool.map(lambda _: read(runtime), range(2)))
    assert peers[0] == peers[1]


@pytest.mark.parametrize("action", ["ESCALATE", "ROAD_DETOUR"])
def test_other_incident_choices_execute_in_the_same_actor(runtime, action):
    approve(runtime)
    send(runtime, "START")
    advance(runtime, 240)
    before = read(runtime)
    candidate = next(c for c in before["candidates"] if c["action"] == action)
    client, store, service, session_id = runtime
    incident = before["incidents"][0]
    branch = client.post(f"/api/v2/sessions/{session_id}/incident-previews", json={
        "contract_version": "2.0", "command_id": uuid4().hex, "idempotency_key": uuid4().hex,
        "session_id": session_id, "command_type": "APPROVE_INCIDENT_OPTION",
        "target_type": "INCIDENT", "target_id": incident["incident_id"],
        "expected_epoch": before["session_epoch"], "expected_revision": incident["aggregate_revision"],
        "payload": {"candidate_id": candidate["candidate_id"]}, "reason": "Compare an authored alternative"})
    assert branch.status_code == 200 and read(runtime) == before
    assert incident_decision(runtime, action)["status"] == "APPLIED"
    if action == "ROAD_DETOUR":
        advance(runtime, 20)
        after = read(runtime)
        assert after["vehicles"][0]["position"] != before["vehicles"][0]["position"]
        route = next(r for r in after["routes"] if r["route_id"] == after["vehicles"][0]["route_id"])
        assert route["directed_edge_ids"] == branch.json()["snapshot"]["routes"][-1]["directed_edge_ids"]
        # Isolate this decision's outcome from subsequent random incidents and
        # accelerate physical travel without changing its approved geometry.
        def accelerate(actor):
            actor.incident_due_tick = 10**12
            for shuttle in actor.shuttles:
                shuttle.cruise_vel *= 8
        stage_boundary(runtime, accelerate)
    # ROAD_DETOUR resolves once shuttle-01 has driven the real bypass round the block
    advance_until(runtime, lambda s: s.incidents[0].state == "RESOLVED", limit=8000)
    assert read(runtime)["incidents"][0]["state"] == "RESOLVED"


def test_continue_is_durable_and_does_not_implicitly_allocate_a_charger(runtime):
    approve(runtime)
    send(runtime, "START")
    snapshot = read(runtime)
    vehicle = snapshot["vehicles"][4]
    option = next(o for o in snapshot["charge_options"] if o["action"] == "CONTINUE")
    args = dict(snapshot=snapshot, target_type="VEHICLE", target_id=vehicle["vehicle_id"],
        revision=vehicle["aggregate_revision"], payload={"option_id": option["option_id"], "option_revision": option["revision"]}, key="continue")
    first = send(runtime, "CONTINUE_SERVICE", **args)
    assert first["status"] == "APPLIED"
    assert send(runtime, "CONTINUE_SERVICE", **args)["command_id"] == first["command_id"]
    advance(runtime, 20)
    after = read(runtime)
    assert after["vehicles"][4]["service_state"] == "IN_SERVICE"
    assert all(not c["occupied_vehicle_ids"] for c in after["chargers"])
    assert after["charge_decision_deferrals"]["shuttle-05"] == option["loops_after"]


def test_failed_domain_commit_does_not_install_a_route_and_retry_uses_original_command(runtime, monkeypatch):
    client, store, service, session_id = runtime
    draft = preview(runtime)
    before = service.sessions[session_id].actor
    original = store.apply
    def fail(*args, **kwargs):
        raise TrainingStoreError("QUEUE_FULL", "Injected failure after durable acceptance")
    monkeypatch.setattr(store, "apply", fail)
    response = client.post(f"/api/v2/sessions/{session_id}/route-approvals", json={
        "preview_id": draft["preview_id"], "preview_revision": draft["preview_revision"],
        "command_id": "original-route-command", "idempotency_key": "route-approval",
        "target_vehicle_ids": ["shuttle-01"], "reason": "Avoid the permanent authored main-road closure"})
    assert response.status_code == 503
    assert service.sessions[session_id].actor is before and read(runtime)["routes"][0]["revision"] == 1
    monkeypatch.setattr(store, "apply", original)
    result = approve(runtime, draft)
    assert result["status"] == "APPLIED" and result["command_id"] == "original-route-command"
    assert read(runtime)["routes"][0]["revision"] == 2


def test_stale_preview_and_conflicting_approvals_cannot_overwrite_live_path(runtime):
    first, second = preview(runtime), preview(runtime)
    client, store, service, session_id = runtime
    def submit(draft):
        return client.post(f"/api/v2/sessions/{session_id}/route-approvals", json={
            "preview_id": draft["preview_id"], "preview_revision": draft["preview_revision"],
            "command_id": uuid4().hex, "idempotency_key": uuid4().hex, "target_vehicle_ids": ["shuttle-01"], "reason": "Conflicting proposal"})
    with ThreadPoolExecutor(max_workers=2) as pool:
        responses = list(pool.map(submit, (first, second)))
    assert sorted(r.status_code for r in responses) == [200, 409]
    assert read(runtime)["routes"][0]["revision"] == 2


def test_two_charger_commands_have_one_effect_and_lost_ack_retry_is_safe(runtime):
    approve(runtime)
    send(runtime, "START")
    snapshot = read(runtime)
    option = next(o for o in snapshot["charge_options"] if o["action"] == "CHARGE_NOW")
    args = dict(snapshot=snapshot, target_type="CHARGER", target_id=option["charger_id"], revision=1,
                payload={"option_id": option["option_id"], "option_revision": option["revision"]})
    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(lambda key: (key, send(runtime, "RESERVE_CHARGER", key=key, **args)), ("first", "second")))
    assert sorted(result["status"] for key, result in results) == ["APPLIED", "REJECTED"]
    key, first = next(pair for pair in results if pair[1]["status"] == "APPLIED")
    retry = send(runtime, "RESERVE_CHARGER", key=key, **args)
    assert retry["command_id"] == first["command_id"] and retry["status"] == "APPLIED"
    assert read(runtime)["chargers"][0]["occupied_vehicle_ids"] == ["shuttle-05"]
    advance(runtime, 20)
    assert read(runtime)["vehicles"][4]["service_state"] == "RETURNING_TO_DEPOT"


def test_decision_after_obstruction_clearance_is_durably_rejected(runtime):
    approve(runtime)
    send(runtime, "START")
    advance(runtime, 240)
    before = read(runtime)
    option = next(c for c in before["candidates"] if c["action"] == "WAIT")
    incident = before["incidents"][0]
    def expire(actor):
        closure = next(d for d in actor.domain.disruptions
                       if d.disruption_id == actor.random_incident["disruption_id"])
        actor.current_tick = closure.active_until_tick-2
    stage_boundary(runtime, expire)
    result = send(runtime, "APPROVE_INCIDENT_OPTION", snapshot=before, target_type="INCIDENT",
        target_id=incident["incident_id"], revision=incident["aggregate_revision"], payload={"candidate_id": option["candidate_id"]})
    assert result["status"] == "REJECTED" and result["failure_code"] == "STALE_REVISION"
    assert read(runtime)["incidents"][0]["state"] == "UNRESOLVED"


def test_alternative_pretrip_path_gets_a_matching_obstruction_and_real_detour(runtime):
    chains = get_workflow_definition().network["chains"]
    draft = preview(runtime, via_node_ids=[only_on("bypass-c")])
    assert draft["approvable"] and draft["directed_edge_ids"] == chains["bypass-c"] + chains["return"]
    approve(runtime, draft)
    send(runtime, "START")
    advance(runtime, 240)
    snapshot = read(runtime)
    selection = runtime[2].sessions[runtime[3]].actor.random_incident
    obstruction = next(d for d in snapshot["disruptions"] if d["disruption_id"] == selection["disruption_id"])
    assert set(obstruction["affected_edge_ids"]).issubset(snapshot["routes"][0]["directed_edge_ids"])
    assert incident_decision(runtime, "ROAD_DETOUR")["status"] == "APPLIED"
    advance(runtime, 20)
    after = read(runtime)
    assert after["vehicles"][0]["position"] != snapshot["vehicles"][0]["position"]
    detour = next(r for r in after["routes"] if r["route_id"] == after["vehicles"][0]["route_id"])
    assert not set(obstruction["affected_edge_ids"]).intersection(detour["directed_edge_ids"])


@pytest.mark.parametrize("missing", ["vehicle", "road"])
def test_unknown_mandatory_route_metadata_prevents_live_approval(runtime, monkeypatch, missing):
    client, store, service, session_id = runtime
    routes = service.sessions[session_id].actor.definition.routes
    if missing == "vehicle":
        monkeypatch.delitem(routes._fixture, "vehicle_width_m")
    else:
        monkeypatch.setitem(routes._network.edges[routes._fixture["detour_directed_edge_ids"][0]], "width_m", None)
    draft = preview(runtime)
    assert draft["validation"] == "UNKNOWN" and not draft["approvable"]
    response = client.post(f"/api/v2/sessions/{session_id}/route-approvals", json={
        "preview_id": draft["preview_id"], "preview_revision": draft["preview_revision"],
        "command_id": uuid4().hex, "idempotency_key": uuid4().hex,
        "target_vehicle_ids": ["shuttle-01"], "reason": "Attempt without mandatory fit metadata"})
    assert response.status_code == 400 and response.json()["error"]["code"] == "UNKNOWN_CONSTRAINT"
    assert read(runtime)["routes"][0]["revision"] == 1


def test_failed_publication_does_not_install_uncommitted_workflow_state(runtime, monkeypatch):
    approve(runtime)
    send(runtime, "START")
    advance(runtime, 1)
    client, store, service, session_id = runtime
    before = service.sessions[session_id].actor
    snapshot = read(runtime)
    def fail(*args, **kwargs):
        raise RuntimeError("Injected workflow commit failure")
    monkeypatch.setattr(store, "commit_snapshot", fail)
    with pytest.raises(RuntimeError, match="workflow commit"):
        advance(runtime, 1)
    assert service.sessions[session_id].actor is before
    assert store.snapshot(session_id).model_dump(mode="json") == snapshot


def test_command_between_presentations_commits_current_progress_without_rewinding(runtime):
    approve(runtime)
    send(runtime, "START")
    client, store, service, session_id = runtime
    before = read(runtime)
    advance(runtime, 1)
    actor = service.sessions[session_id].actor
    assert actor.current_tick == 1 and read(runtime)["tick"] == 0
    progressed = actor.shuttles[4].position.model_copy()
    result = reserve(runtime)
    assert result["status"] == "APPLIED" and read(runtime)["tick"] == 1
    assert read(runtime)["vehicles"][4]["position"] == {"x_m": progressed.x, "y_m": progressed.y, "z_m": 0.}
    assert read(runtime)["vehicles"][4]["position"] != before["vehicles"][4]["position"]
    advance(runtime, 1)
    assert read(runtime)["tick"] == 2 and read(runtime)["vehicles"][4]["service_state"] == "RETURNING_TO_DEPOT"


def test_identical_seed_and_inputs_replay_identical_workflow_state(tmp_path, monkeypatch):
    from contracts.v2.route_edit import RouteApprovalRequest, RouteEditRequest
    from src.services.training_sessions import checkpoint
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)

    async def exercise(path):
        actor = IntegratedSessionActor(1000, session_id="deterministic-integration")
        with TrainingStore(path) as store:
            store.create_session(actor.generate_snapshot(), runtime_state=checkpoint(actor))
            service = TrainingSessionService(store)
            try:
                await service.initialize()
                draft = actor.definition.routes.preview(store.snapshot(actor.session_id), RouteEditRequest(
                    route_id="route-m2-detour", expected_route_revision=1, draft_revision=1))
                result = await service.approve_route("operator", actor.session_id, RouteApprovalRequest(
                    preview_id=draft.preview_id, preview_revision=draft.preview_revision, command_id="route", idempotency_key="route",
                    target_vehicle_ids=["shuttle-01"], reason="Deterministic pre-trip bypass"))
                assert result.status == "APPLIED"
                snapshot = store.snapshot(actor.session_id)
                result = await service.submit("operator", DispatcherCommand(contract_version="2.0", command_id="start", idempotency_key="start",
                    session_id=actor.session_id, command_type="START", expected_epoch=1,
                    expected_revision=snapshot.session.aggregate_revision, target_type="SESSION", target_id=actor.session_id, payload={}))
                assert result.status == "APPLIED"
                for _ in range(240):
                    await service.step(service.sessions[actor.session_id])
                snapshot = store.snapshot(actor.session_id)
                incident = snapshot.incidents[0]
                option = next(c for c in snapshot.candidates if c.action == "WAIT")
                result = await service.submit("operator", DispatcherCommand(contract_version="2.0", command_id="wait", idempotency_key="wait",
                    session_id=actor.session_id, command_type="APPROVE_INCIDENT_OPTION", expected_epoch=1,
                    expected_revision=incident.aggregate_revision, target_type="INCIDENT", target_id=incident.incident_id,
                    payload={"candidate_id": option.candidate_id}, reason="Deterministic wait"))
                assert result.status == "APPLIED"
                hashes = []
                for _ in range(240):
                    await service.step(service.sessions[actor.session_id])
                    hashes.append(service.sessions[actor.session_id].actor._state_hash())
                return hashes
            finally:
                await service.close()
    first = asyncio.run(exercise(tmp_path/"first.sqlite"))
    second = asyncio.run(exercise(tmp_path/"second.sqlite"))
    assert first == second


def test_recovery_preserves_route_hold_and_depot_execution_and_reconfirms_queued_input(tmp_path, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    previous = dict(app.dependency_overrides)
    database = tmp_path/"restart.sqlite"
    try:
        with TrainingStore(database) as store:
            app.dependency_overrides[get_training_store] = lambda: store
            app.dependency_overrides[get_actor_id] = lambda: "integration-operator"
            with TestClient(app) as client:
                created = client.post("/api/v2/sessions", headers={"Idempotency-Key": "recover"}, json={"scenario_revision_id": SCENARIO_SELECTOR, "seed": 1000}).json()
                session_id = created["session_id"]
                runtime = (client, store, app.state.training_services[store], session_id)
                approve(runtime)
                send(runtime, "START")
                reserve(runtime)
                advance(runtime, 260)
                incident_decision(runtime, "ESCALATE")
                before = read(runtime)
                pending = DispatcherCommand(contract_version="2.0", command_id="queued-pause", idempotency_key="queued-pause",
                    session_id=session_id, command_type="PAUSE", expected_epoch=before["session_epoch"],
                    expected_revision=before["session"]["aggregate_revision"], target_type="SESSION", target_id=session_id, payload={})
                store.accept("integration-operator", pending)
        with TrainingStore(database) as store:
            app.dependency_overrides[get_training_store] = lambda: store
            with TestClient(app) as client:
                service = app.state.training_services[store]
                runtime = (client, store, service, session_id)
                after = read(runtime)
                assert after["session"]["status"] == "PAUSED" and after["session_epoch"] == before["session_epoch"]+1
                assert after["tick"] == before["tick"] and after["vehicles"] == before["vehicles"]
                assert after["routes"] == before["routes"] and after["incidents"] == before["incidents"]
                assert store.command_status(session_id, "integration-operator", "queued-pause").status == "NEEDS_RECONFIRMATION"
                assert store.command_status(session_id, "integration-operator", "battery-choice").status == "APPLIED"
                assert send(runtime, "RESUME")["status"] == "APPLIED"
                advance(runtime, 200)
                assert read(runtime)["incidents"][0]["state"] == "RESOLVED"
                assert read(runtime)["vehicles"][4]["position"] != before["vehicles"][4]["position"]
                advance_until(runtime, lambda s: s.vehicles[4].service_state == "CHARGING")
                charge_snapshot = read(runtime)
                assert charge_snapshot["vehicles"][4]["position"] == bay()
                advance(runtime, 20)
        with TrainingStore(database) as store:
            app.dependency_overrides[get_training_store] = lambda: store
            with TestClient(app) as client:
                runtime = (client, store, app.state.training_services[store], session_id)
                assert read(runtime)["vehicles"][4]["service_state"] == "CHARGING"
                send(runtime, "RESUME")
                advance_until(runtime, lambda s: s.vehicles[4].service_state == "STANDBY", limit=1600)
                assert not read(runtime)["chargers"][0]["occupied_vehicle_ids"]
    finally:
        app.dependency_overrides.clear()
        app.dependency_overrides.update(previous)


def stage_boundary(runtime, change):
    """Seed a valid later workflow boundary without thousands of setup ticks."""
    client, store, service, session_id = runtime
    async def stage():
        session = service.sessions[session_id]
        async with session.lock:
            def commit():
                staged = deepcopy(session.actor)
                change(staged)
                staged.current_tick += 2
                staged.command_tick += 2
                baseline = store.snapshot(session_id)
                proposed = service._snapshot(staged, baseline, baseline.session.status)
                committed = publish_workflow(store, staged, proposed, checkpoint)
                return staged, committed
            session.actor, session.baseline = await asyncio.to_thread(commit)
    client.portal.call(stage)


def finish_charge(runtime, shuttle_ids=(4,)):
    def almost_finished(actor):
        for shuttle_id in shuttle_ids:
            shuttle = actor.shuttles[shuttle_id]
            shuttle.progress = actor._routes[shuttle.route_ID]["total_length"]
            shuttle.position = actor._route_evaluation(shuttle.route_ID, shuttle.progress)
            shuttle.curr_vel, shuttle.state = 0, ShuttleState.CHARGING
            actor.modes[shuttle_id] = "CHARGING"
            actor.charge_elapsed[shuttle_id] = actor.definition.data["charge_s"]-actor.STEP_S
            actor.charge_started_kwh[shuttle_id] = shuttle.battery*actor.BATTERY_CAPACITY_KWH/100-.7
    stage_boundary(runtime, almost_finished)
    advance(runtime, 2)


@pytest.mark.parametrize("approved", [False, True])
@pytest.mark.parametrize("legacy_paused", [False, True])
def test_pretrip_recovery_stays_ready_and_can_approve_and_start(tmp_path, monkeypatch, approved, legacy_paused):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    previous = dict(app.dependency_overrides)
    database = tmp_path/"pretrip.sqlite"
    try:
        with TrainingStore(database) as store:
            app.dependency_overrides[get_training_store] = lambda: store
            app.dependency_overrides[get_actor_id] = lambda: "integration-operator"
            with TestClient(app) as client:
                created = client.post("/api/v2/sessions", headers={"Idempotency-Key": "pretrip"},
                    json={"scenario_revision_id": SCENARIO_SELECTOR, "seed": 1000}).json()
                session_id = created["session_id"]
                runtime = client, store, app.state.training_services[store], session_id
                if approved:
                    approve(runtime)
                before = read(runtime)
                store.accept("integration-operator", DispatcherCommand(contract_version="2.0",
                    command_id="queued-start", idempotency_key="queued-start", session_id=session_id,
                    command_type="START", expected_epoch=before["session_epoch"],
                    expected_revision=before["session"]["aggregate_revision"],
                    target_type="SESSION", target_id=session_id, payload={}))
                if legacy_paused:
                    # Older recovery wrote PAUSED publicly but left the private
                    # pre-trip checkpoint READY. Retain matching stored hashes.
                    saved = deepcopy(before)
                    saved["session"]["status"] = "PAUSED"
                    raw = json.dumps(saved)
                    digest = hashlib.sha256(raw.encode()).hexdigest()
                    def save_legacy(connection):
                        connection.execute("UPDATE sessions SET status='PAUSED',snapshot_json=?,state_hash=? WHERE session_id=?",
                                           (raw, digest, session_id))
                        connection.execute("UPDATE checkpoints SET snapshot_json=?,state_hash=? WHERE session_id=? AND event_sequence=?",
                                           (raw, digest, session_id, before["event_sequence"]))
                        connection.commit()
                    store._submit(save_legacy)
        # Repeated restarts must not turn an unstarted attempt into PAUSED.
        for attempt in range(2):
            with TrainingStore(database) as store:
                app.dependency_overrides[get_training_store] = lambda: store
                with TestClient(app) as client:
                    runtime = client, store, app.state.training_services[store], session_id
                    after = read(runtime)
                    assert after["session"]["status"] == "READY" and after["tick"] == 0
                    assert after["session_epoch"] == before["session_epoch"]+attempt+1
                    assert after["vehicles"] == before["vehicles"]
                    assert store.command_status(session_id, "integration-operator", "queued-start").status == "NEEDS_RECONFIRMATION"
                    advance(runtime, 2)
                    assert read(runtime) == after
                    if attempt == 1:
                        if not approved:
                            assert send(runtime, "START")["status"] == "REJECTED"
                            assert approve(runtime)["status"] == "APPLIED"
                        assert send(runtime, "START")["status"] == "APPLIED"
                        advance(runtime, 2)
                        running = read(runtime)
                        assert running["session"]["status"] == "RUNNING"
                        assert running["vehicles"][1]["position"] != before["vehicles"][1]["position"]
    finally:
        app.dependency_overrides.clear()
        app.dependency_overrides.update(previous)


def test_reserving_a_port_keeps_other_offers_on_the_integrated_energy_model(runtime):
    approve(runtime)
    send(runtime, "START")
    stage_boundary(runtime, make_low(3))
    assert reserve(runtime)["status"] == "APPLIED"
    snapshot = read(runtime)
    offer = next(o for o in snapshot["charge_options"] if o["vehicle_id"] == "shuttle-04" and o["action"] == "CHARGE_NOW")
    forecast = next(f for f in snapshot["energy_forecasts"] if f["vehicle_id"] == "shuttle-04")
    assert offer["charger_id"] == "charger-2"
    # Plus the drive from bay 2 to shuttle-04's parking space in the interchange yard
    actor = runtime[2].sessions[runtime[3]].actor
    yard_s = actor._path_distance(actor._parking_points(3, "charger-2"))/actor.definition.data["speed_mps"]
    assert offer["out_of_service_s"] == pytest.approx(forecast["next_depot_eta_s"]+60+yard_s)
    assert 6 < offer["battery_after_kwh"] < 8
    assert any("Charge for 60 s" in assumption for assumption in offer["assumptions"])
    assert reserve(runtime, "shuttle-04", "second-battery-choice")["status"] == "APPLIED"
    store = runtime[1]
    event = next(e for e in store.entity_events(runtime[3], "charger-2", 100) if e.event_type == "CHARGER_RESERVED")
    assert event.data["battery_after_kwh"] == offer["battery_after_kwh"]
    assert event.data["out_of_service_s"] == offer["out_of_service_s"]


def test_a_finished_shuttle_clears_its_bay_before_the_port_is_reused(runtime):
    approve(runtime)
    send(runtime, "START")
    assert reserve(runtime)["status"] == "APPLIED"
    finish_charge(runtime)
    leaving = read(runtime)
    assert leaving["vehicles"][4]["service_state"] == "RETURNING_TO_DEPOT"
    assert leaving["chargers"][0]["occupied_vehicle_ids"] == ["shuttle-05"]
    position = leaving["vehicles"][4]["position"]
    advance(runtime, 2)
    moved = read(runtime)["vehicles"][4]["position"]
    assert 0 < math.hypot(moved["x_m"]-position["x_m"], moved["y_m"]-position["y_m"]) <= 1.000001
    advance_until(runtime, lambda s: s.vehicles[4].service_state == "STANDBY", limit=400)
    parked = read(runtime)
    assert not parked["chargers"][0]["occupied_vehicle_ids"]
    assert parked["vehicles"][4]["position"] == parking(4)
    stage_boundary(runtime, make_low(3))
    assert reserve(runtime, "shuttle-04", "reuse-bay")["status"] == "APPLIED"
    assert read(runtime)["chargers"][0]["occupied_vehicle_ids"] == ["shuttle-04"]
    def approach_bay(actor):
        shuttle = actor.shuttles[3]
        shuttle.progress = actor._routes[shuttle.route_ID]["total_length"]-20
        shuttle.position = actor._route_evaluation(shuttle.route_ID, shuttle.progress)
    stage_boundary(runtime, approach_bay)
    advance_until(runtime, lambda s: s.vehicles[3].service_state == "CHARGING", limit=100)
    assert read(runtime)["vehicles"][3]["position"] == bay()


def test_repeat_charging_uses_yard_distance_and_enforces_the_reserve(runtime):
    approve(runtime)
    send(runtime, "START")
    assert reserve(runtime)["status"] == "APPLIED"
    finish_charge(runtime)
    advance_until(runtime, lambda s: s.vehicles[4].service_state == "STANDBY", limit=400)
    stage_boundary(runtime, lambda actor: setattr(actor.shuttles[4], "battery", 6.0001*100/actor.BATTERY_CAPACITY_KWH))
    snapshot = read(runtime)
    forecast = snapshot["energy_forecasts"][4]
    assert forecast["next_depot_eta_s"] > 0 and not forecast["reaches_depot_above_reserve"]
    option = next(o for o in snapshot["charge_options"] if o["vehicle_id"] == "shuttle-05" and o["action"] == "CHARGE_NOW")
    result = send(runtime, "RESERVE_CHARGER", snapshot=snapshot, target_type="CHARGER", target_id=option["charger_id"],
        revision=snapshot["chargers"][0]["aggregate_revision"], payload={"option_id": option["option_id"], "option_revision": option["revision"]})
    assert result["status"] == "REJECTED"
    assert read(runtime)["vehicles"][4]["service_state"] == "STANDBY"
    assert not read(runtime)["chargers"][0]["occupied_vehicle_ids"]
    stage_boundary(runtime, lambda actor: setattr(actor.shuttles[4], "battery", 6.1*100/actor.BATTERY_CAPACITY_KWH))
    before = read(runtime)
    assert reserve(runtime, key="repeat-charge")["status"] == "APPLIED"
    after = read(runtime)
    assert after["energy_forecasts"][4]["reaches_depot_above_reserve"]
    assert 0 < after["energy_forecasts"][4]["next_depot_eta_s"] <= before["energy_forecasts"][4]["next_depot_eta_s"] < 20
    actor = runtime[2].sessions[runtime[3]].actor
    assert actor._routes[actor.shuttles[4].route_ID]["total_length"] < 200


def test_recovery_mid_parking_retains_the_bay_reservation_until_standby(runtime, tmp_path):
    approve(runtime)
    send(runtime, "START")
    reserve(runtime)
    finish_charge(runtime)
    advance(runtime, 10)
    before = read(runtime)
    database = tmp_path/"parking-recovery.sqlite"
    with closing(sqlite3.connect(runtime[1].path)) as source, closing(sqlite3.connect(database)) as target:
        source.backup(target)
    async def recover():
        with TrainingStore(database) as store:
            service = TrainingSessionService(store)
            try:
                await service.initialize()
                session = service.sessions[runtime[3]]
                recovered = store.snapshot(runtime[3])
                assert recovered.session.status == "PAUSED"
                assert recovered.vehicles[4].position.model_dump() == before["vehicles"][4]["position"]
                assert session.actor.parking_chargers == {4: "charger-1"}
                command = DispatcherCommand(contract_version="2.0", command_id="resume-parking", idempotency_key="resume-parking",
                    session_id=runtime[3], command_type="RESUME", expected_epoch=recovered.session_epoch,
                    expected_revision=recovered.session.aggregate_revision, target_type="SESSION", target_id=runtime[3], payload={})
                assert (await service.submit("integration-operator", command)).status == "APPLIED"
                for _ in range(400):
                    if store.snapshot(runtime[3]).vehicles[4].service_state == "STANDBY":
                        break
                    assert store.snapshot(runtime[3]).chargers[0].occupied_vehicle_ids == ["shuttle-05"]
                    await service.step(session)
                finished = store.snapshot(runtime[3])
                assert finished.vehicles[4].service_state == "STANDBY"
                assert not finished.chargers[0].occupied_vehicle_ids and not session.actor.parking_chargers
                assert finished.vehicles[4].position.model_dump() == parking(4)
            finally:
                await service.close()
    asyncio.run(recover())


def test_a_real_pause_before_the_first_tick_still_recovers_paused(runtime, tmp_path):
    approve(runtime)
    send(runtime, "START")
    send(runtime, "PAUSE")
    database = tmp_path/"paused-at-zero.sqlite"
    with closing(sqlite3.connect(runtime[1].path)) as source, closing(sqlite3.connect(database)) as target:
        source.backup(target)
    with TrainingStore(database) as store:
        assert store.snapshot(runtime[3]).session.status == "PAUSED"


def test_a_legacy_parked_shuttle_clears_without_releasing_the_next_reservation(runtime):
    approve(runtime)
    send(runtime, "START")
    reserve(runtime)
    def legacy_finish(actor):
        shuttle = actor.shuttles[4]
        shuttle.progress = actor._routes[shuttle.route_ID]["total_length"]
        shuttle.position = actor._route_evaluation(shuttle.route_ID, shuttle.progress)
        shuttle.curr_vel, shuttle.state = 0, ShuttleState.DEPOT_STANDBY
        actor.modes[4] = "STANDBY"
        actor.domain.chargers[0].occupied_vehicle_ids = []
        actor.domain.chargers[0].aggregate_revision += 1
        actor.shuttles[3].battery = low_battery_kwh(actor, 3)*100/actor.BATTERY_CAPACITY_KWH
    stage_boundary(runtime, legacy_finish)
    assert reserve(runtime, "shuttle-04", "legacy-bay-reuse")["status"] == "APPLIED"
    def approach(actor):
        shuttle = actor.shuttles[3]
        shuttle.progress = actor._routes[shuttle.route_ID]["total_length"]-20
        shuttle.position = actor._route_evaluation(shuttle.route_ID, shuttle.progress)
    stage_boundary(runtime, approach)
    advance_until(runtime, lambda s: s.vehicles[3].service_state == "CHARGING", limit=100)
    advance_until(runtime, lambda s: s.vehicles[4].service_state == "STANDBY", limit=400)
    parked = read(runtime)
    assert parked["vehicles"][4]["position"] == parking(4)
    assert parked["chargers"][0]["occupied_vehicle_ids"] == ["shuttle-04"]


def test_simultaneous_opposing_bay_departures_both_reach_parking(runtime):
    approve(runtime)
    send(runtime, "START")
    reserve(runtime)
    stage_boundary(runtime, make_low(0))
    assert reserve(runtime, "shuttle-01", "other-bay")["status"] == "APPLIED"
    finish_charge(runtime, (0, 4))
    client, store, service, session_id = runtime
    async def park_both():
        for _ in range(200):
            snapshot = store.snapshot(session_id)
            a, b = snapshot.vehicles[0].position, snapshot.vehicles[4].position
            assert math.hypot(a.x_m-b.x_m, a.y_m-b.y_m) >= IntegratedSessionActor.SEPARATION_M-1e-8
            if snapshot.vehicles[0].service_state == snapshot.vehicles[4].service_state == "STANDBY":
                assert all(not c.occupied_vehicle_ids for c in snapshot.chargers)
                return
            await service.step(service.sessions[session_id])
            await service.step(service.sessions[session_id])
        raise AssertionError("Simultaneous bay departures did not clear both ports")
    client.portal.call(park_both)
