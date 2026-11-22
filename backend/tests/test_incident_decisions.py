"""Decisions per exposed shuttle in a running integrated session: what each option does to the shuttle it is for.

Each test schedules one world event on shuttle-01's route, beyond its next stop, so that it can keep its route,
take a detour round it, hold at that stop or request assistance. The sessions run without background traffic
(conftest.without_traffic), where an accident's queue is the speed-factor fallback.
"""

import pytest

from contracts.v2.models import Disruption

from backend.tests.test_training_integration import advance, advance_until, approve, preview, read, runtime, send, stage_boundary
from src.services.incident_scenarios.world_events import ScheduledEvent
from src.services.incidents import detour_route_id, is_detour_of

__all__ = ["runtime"]

INCIDENT = "scheduled-e1"


# An accident's queue reaches this far back up the road from its scene, and further: a road this far past a
# stop has its whole footprint beyond the stop
QUEUE_CLEAR_M = 400.0


def detourable(actor, edge, index=0):
    """Whether a shuttle (shuttle-01 unless index says otherwise) has a way round the edge from where it is and
    can still finish its loop."""
    hypothetical = actor.generate_snapshot()
    hypothetical.disruptions.append(Disruption(
        disruption_id="test-closure", aggregate_revision=1, affected_edge_ids=[edge],
        active_from_tick=actor.current_tick, active_until_tick=actor.current_tick + 1,
        reason="Find a road with a detour round it", provenance=hypothetical.source))
    vehicle = hypothetical.vehicles[index]
    routes = actor.definition.routes.for_route(hypothetical, vehicle.route_id)
    return routes.continuation_preview(hypothetical, vehicle.vehicle_id).approvable


def schedule(runtime, monkeypatch, event_type="COLLISION", *, duration_s=200.0):
    """Starts the session and schedules one event on a road of shuttle-01's route, with a way round it, whose
    queue starts past one of its stops. Returns the event's edge and the stop's progress along the route."""
    client, store, service, session_id = runtime
    approve(runtime)
    send(runtime, "START")
    advance(runtime, 20)
    actor = service.sessions[session_id].actor
    shuttle = actor.shuttles[0]
    stops = sorted(stop for stop in actor.stop_progress[shuttle.route_ID] if stop > shuttle.progress + 20)
    target = actor._incident_target(shuttle, set())
    starts = sorted((shuttle.progress + ahead, edge) for edge, ahead in target["ahead"])
    stop, edge = next((s, e) for start, e in starts for s in stops[:1]
                      if start > s + QUEUE_CLEAR_M and detourable(actor, e))
    at = (actor.current_tick + 4) * actor.STEP_S
    monkeypatch.setattr(actor.incident_catalog, "timeline", [ScheduledEvent("e1", event_type, edge, at, duration_s)])
    stage_boundary(runtime, lambda a: setattr(a, "incident_due_tick", 10**9))
    advance(runtime, 6)
    return edge, stop


def incident(snapshot):
    return next(i for i in snapshot["incidents"] if i["incident_id"] == INCIDENT)


def decide(runtime, action, vehicle_id="shuttle-01", *, key=None):
    snapshot = read(runtime)
    found = incident(snapshot)
    option = next(c for c in snapshot["candidates"]
                  if c["incident_id"] == INCIDENT and c["vehicle_id"] == vehicle_id and c["action"] == action)
    assert option["validation"] == "PASS", option
    return send(runtime, "APPROVE_INCIDENT_OPTION", snapshot=snapshot, target_type="INCIDENT", target_id=INCIDENT,
                revision=found["aggregate_revision"], payload={"candidate_id": option["candidate_id"]},
                key=key or f"{vehicle_id}-{action}")


def test_an_exposed_shuttle_is_offered_every_option_with_its_delay_and_its_stop(runtime, monkeypatch):
    client, store, service, session_id = runtime
    edge, stop = schedule(runtime, monkeypatch)
    snapshot = read(runtime)
    found = incident(snapshot)
    assert found["state"] == "OPTIONS_READY" and found["vehicle_id"] is None
    exposure = next(e for e in found["exposures"] if e["vehicle_id"] == "shuttle-01")
    # Without traffic the queue moves at half cruising speed: keeping the route costs time
    assert exposure["keep_route_delay_s"] > 0
    record = next(r for r in service.sessions[session_id].actor.random_incidents if r["incident_id"] == INCIDENT)
    hold = next(e for e in record["exposures"] if e["vehicle_id"] == "shuttle-01")["hold_stop"]
    assert hold["progress_m"] == pytest.approx(stop, abs=1e-3) and hold["distance_m"] < exposure["distance_m"]
    offered = [c["action"] for c in snapshot["candidates"] if c["vehicle_id"] == "shuttle-01"]
    assert offered == ["KEEP_ROUTE", "ROAD_DETOUR", "ESCALATE", "HOLD_AT_STOP"]


def test_a_detour_round_a_collision_keeps_off_its_road(runtime, monkeypatch):
    client, store, service, session_id = runtime
    edge, _ = schedule(runtime, monkeypatch)
    result = decide(runtime, "ROAD_DETOUR")
    assert result["status"] == "APPLIED", result
    snapshot = read(runtime)
    shuttle = snapshot["vehicles"][0]
    assert shuttle["service_state"] == "DETOURING" and shuttle["route_id"] == detour_route_id(INCIDENT, "shuttle-01")
    route = next(r for r in snapshot["routes"] if r["route_id"] == shuttle["route_id"])
    assert edge not in route["directed_edge_ids"]
    assert incident(snapshot)["state"] == "EXECUTING"
    assert [r["action"] for r in incident(snapshot)["responses"]] == ["ROAD_DETOUR"]
    # The actor drives the detour as the shuttle's route, and it stays DETOURING after a stop
    actor = service.sessions[session_id].actor
    assert is_detour_of(actor.shuttles[0].route_ID, INCIDENT)
    advance_until(runtime, lambda s: s.vehicles[0].service_state == "DWELLING", limit=4000)
    advance_until(runtime, lambda s: s.vehicles[0].service_state != "DWELLING", limit=4000)
    assert read(runtime)["vehicles"][0]["service_state"] == "DETOURING"


def test_a_drafted_detour_round_a_collision_answers_the_live_closures(runtime, monkeypatch):
    # The planner closes the collision's road for a moment to go round it. The preview must still report the
    # session's own closures, or the client takes it for stale and never lets the operator approve it.
    edge, _ = schedule(runtime, monkeypatch)
    snapshot = read(runtime)
    route = next(r for r in snapshot["routes"] if r["route_id"] == snapshot["vehicles"][0]["route_id"])
    drafted = preview(runtime, route_id=route["route_id"], expected_route_revision=route["revision"])
    assert drafted["approvable"], drafted["checks"]
    assert edge not in drafted["directed_edge_ids"]
    assert drafted["closure_revisions"] == {d["disruption_id"]: d["aggregate_revision"] for d in snapshot["disruptions"]}


def test_a_detour_planned_round_a_scene_reports_the_sessions_closures(runtime, monkeypatch):
    # Every caller that plans round an incident's scene (a drafted detour, the offered ROAD_DETOUR option) passes
    # the session's snapshot as live, so the planner's temporary closure never reaches the preview.
    client, store, service, session_id = runtime
    edge, _ = schedule(runtime, monkeypatch)
    actor = service.sessions[session_id].actor
    live = actor.generate_snapshot()
    planning = live.model_copy(deep=True)
    planning.disruptions.append(Disruption(
        disruption_id="scene", aggregate_revision=1, affected_edge_ids=[edge],
        active_from_tick=live.tick, active_until_tick=live.tick + 1, reason="Plan round the scene", provenance=live.source))
    routes = actor.definition.routes.for_route(live, live.vehicles[0].route_id)
    assert "scene" in routes.continuation_preview(planning, "shuttle-01").closure_revisions
    planned = routes.continuation_preview(planning, "shuttle-01", live=live)
    assert edge not in planned.directed_edge_ids
    assert planned.closure_revisions == {d.disruption_id: d.aggregate_revision for d in live.disruptions}


def test_assistance_shortens_a_collision_for_the_traffic_and_everyone(runtime, monkeypatch):
    client, store, service, session_id = runtime
    schedule(runtime, monkeypatch, duration_s=400.0)
    before = incident(read(runtime))
    assert decide(runtime, "ESCALATE")["status"] == "APPLIED"
    after = incident(read(runtime))
    assert after["clears_at_tick"] < before["clears_at_tick"]
    actor = service.sessions[session_id].actor
    record = next(r for r in actor.random_incidents if r["incident_id"] == INCIDENT)
    assert actor._incident_window(record)[1] == after["clears_at_tick"]
    advance_until(runtime, lambda s: next(i for i in s.incidents if i.incident_id == INCIDENT).state == "RESOLVED",
                  limit=2*(after["clears_at_tick"] - read(runtime)["tick"]) + 20)
    assert read(runtime)["tick"] <= after["clears_at_tick"] + 4


def test_a_shuttle_told_to_hold_waits_at_its_stop_until_the_incident_clears(runtime, monkeypatch):
    client, store, service, session_id = runtime
    edge, stop = schedule(runtime, monkeypatch, duration_s=260.0)
    assert decide(runtime, "HOLD_AT_STOP")["status"] == "APPLIED"
    clears = incident(read(runtime))["clears_at_tick"]
    def at_stop(_):
        shuttle = service.sessions[session_id].actor.shuttles[0]
        return abs(shuttle.progress - stop) < 0.01
    # It drives on past its loop-start dwell, which it was not told to hold at
    advance_until(runtime, at_stop, limit=8000)
    advance(runtime, 2)  # its arrival is published at the next boundary
    actor = service.sessions[session_id].actor
    arrived = read(runtime)
    assert arrived["vehicles"][0]["service_state"] == "DWELLING"
    held_at = actor.shuttles[0].progress
    assert held_at == pytest.approx(stop, abs=0.01)
    # Well past an ordinary dwell, it is still there
    advance(runtime, round(3*actor.DWELL_S/actor.STEP_S))
    assert read(runtime)["vehicles"][0]["service_state"] == "DWELLING"
    assert service.sessions[session_id].actor.shuttles[0].progress == held_at
    assert read(runtime)["tick"] < clears
    advance_until(runtime, lambda s: s.vehicles[0].service_state == "IN_SERVICE", limit=2*(clears - arrived["tick"]) + 400)
    assert read(runtime)["tick"] >= clears
    responses = incident(read(runtime))["responses"]
    assert responses[0]["action"] == "HOLD_AT_STOP" and responses[0]["completed_tick"] is not None
    assert responses[0]["hold_stop_id"] == "52449-toa-payoh-police-centre"


def test_a_shuttle_that_drives_into_a_collision_undecided_has_made_its_decision(runtime, monkeypatch):
    client, store, service, session_id = runtime
    schedule(runtime, monkeypatch, duration_s=300.0)
    advance_until(runtime, lambda s: any(r.vehicle_id == "shuttle-01" for i in s.incidents
                                         if i.incident_id == INCIDENT for r in i.responses), limit=6000)
    snapshot = read(runtime)
    response = next(r for r in incident(snapshot)["responses"] if r["vehicle_id"] == "shuttle-01")
    assert response["action"] == "NO_DECISION" and response["before_arrival"] is False
    missed = [e for e in store.entity_events(session_id, INCIDENT, snapshot["event_sequence"])
              if e.event_type == "INCIDENT_RESPONSE_MISSED"]
    assert missed and missed[0].data["assessment"]["total"] == 0
    # A late decision is still taken, and replaces it
    assert decide(runtime, "KEEP_ROUTE", key="late")["status"] == "APPLIED"
    late = next(r for r in incident(read(runtime))["responses"] if r["vehicle_id"] == "shuttle-01")
    assert late["action"] == "KEEP_ROUTE" and late["before_arrival"] is False


@pytest.mark.parametrize("index", [1, 2])
def test_a_peer_shuttle_is_offered_a_detour_and_drives_it_serving_its_own_stops(runtime, monkeypatch, index):
    """Every shuttle's route is drawn on the road network, so the route editor takes a peer round an incident on
    its route as it does shuttle-01: one shuttle of each peer loop."""
    client, store, service, session_id = runtime
    approve(runtime)
    send(runtime, "START")
    advance(runtime, 20)
    actor = service.sessions[session_id].actor
    shuttle = actor.shuttles[index]
    vehicle_id = actor.domain.vehicles[index].vehicle_id
    home = actor.domain.vehicles[index].route_id
    target = actor._incident_target(shuttle, set())
    assert target["editable"]
    # A collision on a road of its route well ahead, with a way round it
    edge = next(e for e, ahead in target["ahead"] if ahead > 100 and detourable(actor, e, index))
    at = (actor.current_tick + 4) * actor.STEP_S
    monkeypatch.setattr(actor.incident_catalog, "timeline", [ScheduledEvent("e1", "COLLISION", edge, at, 200.0)])
    stage_boundary(runtime, lambda a: setattr(a, "incident_due_tick", 10**9))
    advance(runtime, 6)
    snapshot = read(runtime)
    offered = [c["action"] for c in snapshot["candidates"] if c["vehicle_id"] == vehicle_id and c["incident_id"] == INCIDENT]
    assert "ROAD_DETOUR" in offered
    stops = next(r for r in snapshot["routes"] if r["route_id"] == home)["required_stop_ids"]
    assert decide(runtime, "ROAD_DETOUR", vehicle_id)["status"] == "APPLIED"
    snapshot = read(runtime)
    vehicle = snapshot["vehicles"][index]
    assert vehicle["service_state"] == "DETOURING" and vehicle["route_id"] == detour_route_id(INCIDENT, vehicle_id)
    route = next(r for r in snapshot["routes"] if r["route_id"] == vehicle["route_id"])
    assert edge not in route["directed_edge_ids"] and route["required_stop_ids"] == stops
    # The actor drives the detour and pulls in at the peer's own stops, not shuttle-01's
    actor = service.sessions[session_id].actor
    assert is_detour_of(actor.shuttles[index].route_ID, INCIDENT)
    names = dict(actor.stop_names[vehicle["route_id"]])
    assert names.pop("end") == stops[-1] and set(names.values()) <= set(stops)
    advance_until(runtime, lambda s: s.vehicles[index].service_state == "DWELLING", limit=4000)
    advance_until(runtime, lambda s: s.vehicles[index].service_state != "DWELLING", limit=4000)
    assert read(runtime)["vehicles"][index]["service_state"] == "DETOURING"
