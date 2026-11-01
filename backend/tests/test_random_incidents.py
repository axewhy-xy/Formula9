"""Random incident HTTP decisions, motion, lifecycle and durable recovery."""

import math
from uuid import uuid4

import pytest

from backend.tests.test_training_integration import (
    advance, approve, read, runtime, send, stage_boundary,
)
from src.services.training_sessions import TrainingSessionService


@pytest.mark.parametrize("action", ["WAIT", "ESCALATE", "ROAD_DETOUR"])
def test_random_incident_preview_and_approval_share_the_checkpointed_instance(runtime, action):
    client, store, service, session_id = runtime
    approve(runtime)
    send(runtime, "START")
    advance(runtime, 240)
    before = read(runtime)
    incident = before["incidents"][0]
    candidate = next(c for c in before["candidates"] if c["action"] == action)
    assert candidate["validation"] == "PASS"
    command = dict(contract_version="2.0", command_id=uuid4().hex, idempotency_key=uuid4().hex,
                   session_id=session_id, command_type="APPROVE_INCIDENT_OPTION",
                   expected_epoch=before["session_epoch"], expected_revision=incident["aggregate_revision"],
                   target_type="INCIDENT", target_id=incident["incident_id"],
                   payload={"candidate_id": candidate["candidate_id"]}, reason="Review a randomized obstruction")
    preview = client.post(f"/api/v2/sessions/{session_id}/incident-previews", json=command)
    assert preview.status_code == 200, preview.text
    assert read(runtime) == before
    # The stopped actor must agree with the durable position before and after a restart.
    restored = TrainingSessionService._restore_session(*store.runtime(session_id))
    assert restored.actor.generate_snapshot().vehicles == store.snapshot(session_id).vehicles
    decision = client.post(f"/api/v2/sessions/{session_id}/commands", json=command)
    assert decision.status_code == 202 and decision.json()["status"] == "APPLIED", decision.text
    assert client.post(f"/api/v2/sessions/{session_id}/commands", json=command).json() == decision.json()
    if action == "ROAD_DETOUR":
        after = read(runtime)
        assert after["vehicles"][0]["position"] == before["vehicles"][0]["position"]
        route = next(r for r in after["routes"] if r["route_id"] == after["vehicles"][0]["route_id"])
        closure = service.sessions[session_id].actor.random_incident["affected_edge_ids"]
        assert not set(closure).intersection(route["directed_edge_ids"])
        advance(runtime, 2)
        moved = read(runtime)["vehicles"][0]["position"]
        prior = after["vehicles"][0]["position"]
        assert 0 < math.dist(tuple(prior.values()), tuple(moved.values())) <= 1.01
    restored = TrainingSessionService._restore_session(*store.runtime(session_id))
    assert restored.actor.generate_snapshot().vehicles == store.snapshot(session_id).vehicles
    send(runtime, "ARCHIVE")
    # Evidence reads must still resolve the random config when the live actor is gone.
    for view in ("history", "debrief"):
        response = client.get(f"/api/v2/sessions/{session_id}/incidents/{incident['incident_id']}/{view}")
        assert response.status_code == 200, response.text


@pytest.mark.parametrize("kind", ["ROAD_ACCIDENT", "ROAD_BLOCK"])
@pytest.mark.parametrize("legacy_state", [False, True])
def test_informational_incidents_slow_motion_and_resolve_once_without_closing_accident_roads(
        runtime, kind, legacy_state):
    client, store, service, session_id = runtime
    approve(runtime)
    send(runtime, "START")

    def seed(actor):
        route = actor.domain.routes[0]
        incident_edge = (route.directed_edge_ids[0] if kind == "ROAD_ACCIDENT" else
                         next(e for e in actor.definition.data["edges"] if e not in route.directed_edge_ids))
        selection = dict(incident_id="random-informational", disruption_id="random-info-disruption",
                         kind=kind, vehicle_id="shuttle-01", route_id=route.route_id,
                         affected_edge_ids=[incident_edge], impact_edge_ids=[route.directed_edge_ids[0]],
                         requires_guidance=False, speed_factor=0.5, trigger_tick=actor.current_tick+2,
                         clear_after_ticks=10)
        actor.incident_due_tick = 10**9
        actor.random_incidents = [selection]
        actor.random_incident = selection

    stage_boundary(runtime, seed)
    detected = store.snapshot(session_id)
    assert detected.incidents[0].state == "DETECTED"
    disruptions = [d for d in detected.disruptions if d.disruption_id == "random-info-disruption"]
    assert bool(disruptions) == (kind == "ROAD_BLOCK")
    # Slowdown-only accidents remain available in the route editor.
    context = client.get(f"/api/v2/sessions/{session_id}/routes/route-m2-detour/editor").json()
    occupied = detected.routes[0].directed_edge_ids[0]
    assert not next(e for e in context["edges"] if e["edge_id"] == occupied)["closed"]
    advance(runtime, 2)
    progress = service.sessions[session_id].actor.shuttles[0].progress
    advance(runtime, 2)
    actor = service.sessions[session_id].actor
    actual_speed = (actor.shuttles[0].progress-progress)/(2*actor.STEP_S)
    assert actual_speed == pytest.approx(5.0)
    assert store.snapshot(session_id).vehicles[0].speed_mps == pytest.approx(actual_speed)
    if legacy_state:
        stage_boundary(runtime, lambda a: setattr(a.domain.incidents[0], "state", "UNRESOLVED"))
    advance(runtime, 8)
    assert store.snapshot(session_id).incidents[0].state == "RESOLVED"
    events = store.entity_events(session_id, "random-informational", store.snapshot(session_id).event_sequence)
    assert sum(e.event_type == "INCIDENT_RESOLVED" for e in events) == 1
    assert store.snapshot(session_id).vehicles[0].speed_mps == pytest.approx(10.0)


def test_a_closure_on_the_occupied_road_cannot_offer_a_passing_detour(runtime):
    client, _, _, session_id = runtime
    approve(runtime)
    send(runtime, "START")
    advance(runtime, 2)

    def seed(actor):
        route = actor.domain.routes[0]
        selection = dict(incident_id="random-occupied-road", disruption_id="occupied-road-closure",
                         kind="ROAD_BLOCK", vehicle_id="shuttle-01", route_id=route.route_id,
                         affected_edge_ids=[route.directed_edge_ids[0]], impact_edge_ids=[],
                         requires_guidance=True, trigger_tick=actor.current_tick+2,
                         clear_after_ticks=100, block_current_route_entrance=False)
        actor.incident_due_tick = 10**9
        actor.random_incidents = [selection]
        actor.random_incident = selection

    stage_boundary(runtime, seed)
    before = read(runtime)
    incident = before["incidents"][0]
    candidate = next(c for c in before["candidates"] if c["action"] == "ROAD_DETOUR")
    assert candidate["validation"] == "FAIL"
    result = send(runtime, "APPROVE_INCIDENT_OPTION", target_type="INCIDENT",
                  target_id=incident["incident_id"], revision=incident["aggregate_revision"],
                  payload={"candidate_id": candidate["candidate_id"]})
    assert result["status"] == "REJECTED"
    after = read(runtime)
    for field in ("tick", "vehicles", "routes", "incidents", "candidates", "disruptions"):
        assert after[field] == before[field]
    assert after["event_sequence"] == before["event_sequence"]+1  # The denied attempt is journaled.


def test_settled_incidents_leave_the_checkpoint_but_keep_their_evidence(runtime):
    client, store, service, session_id = runtime
    approve(runtime)
    send(runtime, "START")
    advance(runtime, 240)
    before = read(runtime)
    incident = before["incidents"][0]
    candidate = next(c for c in before["candidates"] if c["action"] == "WAIT")
    command = dict(contract_version="2.0", command_id=uuid4().hex, idempotency_key=uuid4().hex,
                   session_id=session_id, command_type="APPROVE_INCIDENT_OPTION",
                   expected_epoch=before["session_epoch"], expected_revision=incident["aggregate_revision"],
                   target_type="INCIDENT", target_id=incident["incident_id"],
                   payload={"candidate_id": candidate["candidate_id"]}, reason="Hold until the road clears")
    decision = client.post(f"/api/v2/sessions/{session_id}/commands", json=command)
    assert decision.json()["status"] == "APPLIED", decision.text

    def expire(actor):
        closure = next(d for d in actor.domain.disruptions
                       if d.disruption_id == actor.random_incident["disruption_id"])
        actor.current_tick = closure.active_until_tick-2
        actor.incident_due_tick = 10**9

    stage_boundary(runtime, expire)
    advance(runtime, 4)
    assert read(runtime)["incidents"][0]["state"] == "RESOLVED"
    assert service.sessions[session_id].actor.random_incidents == []
    assert store.runtime(session_id)[1]["workflow"]["random_incidents"] == []

    # A retry after pruning still returns the original receipt, and evidence stays readable.
    retried = client.post(f"/api/v2/sessions/{session_id}/commands", json=command).json()
    for key in ("command_id", "status", "applied_tick", "result_event_id", "target_revision"):
        assert retried[key] == decision.json()[key]
    for view in ("history", "debrief"):
        response = client.get(f"/api/v2/sessions/{session_id}/incidents/{incident['incident_id']}/{view}")
        assert response.status_code == 200, response.text

    # An empty active list must not restart the scripted first-incident path.
    stage_boundary(runtime, lambda actor: setattr(actor, "incident_due_tick", actor.current_tick))
    advance(runtime, 2)
    actor = service.sessions[session_id].actor
    assert actor.random_incident["incident_id"] != incident["incident_id"]
    assert actor.random_incident["clear_after_ticks"] == round(30/actor.STEP_S)
