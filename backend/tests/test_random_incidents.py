"""Random incident HTTP decisions, motion, lifecycle and durable recovery."""

import math
from uuid import uuid4

import pytest

from backend.tests.test_training_integration import (
    advance, approve, read, runtime, send, stage_boundary,
)
from src.services.incidents import NEVER, vehicle_label
from src.services.integrated_actor import IntegratedSessionActor, get_workflow_definition
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


def test_coexisting_incidents_keep_the_session_ticking_and_a_hold_holds(runtime):
    client, store, service, session_id = runtime
    approve(runtime)
    send(runtime, "START")
    advance(runtime, 2)

    def seed(actor):
        route = actor.domain.routes[0]
        accident_edge = next(e for e in actor.definition.data["edges"] if e not in route.directed_edge_ids)
        accident = dict(incident_id="random-accident", disruption_id="random-accident-disruption",
                        kind="ROAD_ACCIDENT", vehicle_id="shuttle-01", route_id=route.route_id,
                        affected_edge_ids=[accident_edge], impact_edge_ids=[], requires_guidance=False,
                        speed_factor=1.0, trigger_tick=actor.current_tick+2, clear_after_ticks=8)
        closure = dict(incident_id="random-closure", disruption_id="random-closure-disruption",
                       kind="ROAD_BLOCK", vehicle_id="shuttle-01", route_id=route.route_id,
                       affected_edge_ids=[route.directed_edge_ids[-1]], impact_edge_ids=[],
                       requires_guidance=True, trigger_tick=actor.current_tick+4,
                       clear_after_ticks=100, block_current_route_entrance=False)
        actor.incident_due_tick = 10**9
        actor.random_incidents = [accident, closure]
        actor.random_incident = closure

    stage_boundary(runtime, seed)
    start = read(runtime)["tick"]
    advance(runtime, 4)
    both = read(runtime)
    assert both["tick"] > start
    assert {i["incident_id"]: i["state"] for i in both["incidents"]} == {
        "random-accident": "DETECTED", "random-closure": "OPTIONS_READY"}
    held = both["vehicles"][0]
    assert held["service_state"] == "AWAITING_GUIDANCE"
    # The informational incident settles on its own while the other keeps holding the shuttle.
    advance(runtime, 8)
    later = read(runtime)
    assert later["tick"] > both["tick"]
    states = {i["incident_id"]: i["state"] for i in later["incidents"]}
    assert states == {"random-accident": "RESOLVED", "random-closure": "OPTIONS_READY"}
    assert later["vehicles"][0]["position"] == held["position"]
    restored = TrainingSessionService._restore_session(*store.runtime(session_id))
    assert restored.actor.generate_snapshot().vehicles == store.snapshot(session_id).vehicles


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


def held_at_a_closure(runtime):
    """Run until the first random closure holds the shuttle, and return the live snapshot."""
    approve(runtime)
    send(runtime, "START")
    advance(runtime, 240)
    before = read(runtime)
    assert before["incidents"][0]["state"] == "OPTIONS_READY"
    return before


def draft_detour(runtime, snapshot, **changes):
    client, _, _, session_id = runtime
    route = next(r for r in snapshot["routes"] if r["route_id"] == snapshot["vehicles"][0]["route_id"])
    response = client.post(f"/api/v2/sessions/{session_id}/route-previews", json={
        "route_id": route["route_id"], "expected_route_revision": route["revision"], "draft_revision": 7, **changes})
    assert response.status_code == 200, response.text
    return response.json()


def reroute(runtime, snapshot, edges, *, key=None):
    incident = snapshot["incidents"][-1]
    candidate = next(c for c in snapshot["candidates"]
                     if c["action"] == "ROAD_DETOUR" and c["incident_id"] == incident["incident_id"])
    return send(runtime, "APPROVE_INCIDENT_OPTION", snapshot=snapshot, target_type="INCIDENT",
                target_id=incident["incident_id"], revision=incident["aggregate_revision"], key=key,
                payload={"candidate_id": candidate["candidate_id"], "directed_edge_ids": edges})


def road_off_the_detour(runtime, snapshot, draft):
    """A road the suggested detour does not use, that an approvable draft can be sent along."""
    client, _, service, session_id = runtime
    route_id = snapshot["vehicles"][0]["route_id"]
    context = client.get(f"/api/v2/sessions/{session_id}/routes/{route_id}/editor").json()
    used = set(draft["directed_edge_ids"])
    for edge in context["edges"]:
        if edge["edge_id"] in used or edge["closed"] or edge["fit"] != "PASS":
            continue
        chosen = draft_detour(runtime, snapshot, waypoint_edge_ids=[edge["edge_id"]])
        if chosen["approvable"] and chosen["directed_edge_ids"] != draft["directed_edge_ids"]:
            return edge["edge_id"], chosen
    raise AssertionError("The network has no other road for a drafted detour")


def test_the_route_editor_opens_for_a_shuttle_held_at_a_random_closure(runtime):
    client, store, service, session_id = runtime
    before = held_at_a_closure(runtime)
    route = next(r for r in before["routes"] if r["route_id"] == before["vehicles"][0]["route_id"])
    closed = {edge for d in before["disruptions"] if d["disruption_id"].startswith("random-") for edge in d["affected_edge_ids"]}
    context = client.get(f"/api/v2/sessions/{session_id}/routes/{route['route_id']}/editor")
    assert context.status_code == 200, context.text
    assert closed <= {e["edge_id"] for e in context.json()["edges"] if e["closed"]}

    # A suggestion keeps what the shuttle has driven, so it starts from where the shuttle stands.
    suggested = draft_detour(runtime, before)
    assert suggested["approvable"] and suggested["preview_revision"] == 7
    assert not closed.intersection(suggested["directed_edge_ids"])
    assert suggested["directed_edge_ids"][0] == route["directed_edge_ids"][0]
    assert all(suggested["edited"]["stop_coverage"].values())
    assert read(runtime) == before  # A preview changes nothing, and the shuttle stays held.

    # So does a drafted one, through the road the operator picked.
    picked, chosen = road_off_the_detour(runtime, before, suggested)
    assert {picked, picked.replace("forward", "#").replace("reverse", "forward").replace("#", "reverse")}.intersection(chosen["directed_edge_ids"])
    assert not closed.intersection(chosen["directed_edge_ids"])
    closed_pick = draft_detour(runtime, before, waypoint_edge_ids=[next(iter(closed))])
    assert not closed_pick["approvable"]
    assert any(c["check"] == "CLOSURE" and c["status"] == "FAIL" for c in closed_pick["checks"])


def test_approving_a_drafted_detour_drives_exactly_those_roads_and_survives_a_restart(runtime):
    client, store, service, session_id = runtime
    before = held_at_a_closure(runtime)
    _, chosen = road_off_the_detour(runtime, before, draft_detour(runtime, before))
    decision = reroute(runtime, before, chosen["directed_edge_ids"], key="drafted-detour")
    assert decision["status"] == "APPLIED", decision
    assert reroute(runtime, before, chosen["directed_edge_ids"], key="drafted-detour")["command_id"] == decision["command_id"]

    after = read(runtime)
    vehicle = after["vehicles"][0]
    assert vehicle["service_state"] == "DETOURING" and vehicle["position"] == before["vehicles"][0]["position"]
    route = next(r for r in after["routes"] if r["route_id"] == vehicle["route_id"])
    assert route["directed_edge_ids"] == chosen["directed_edge_ids"]
    assert after["incidents"][0]["state"] == "EXECUTING"
    events = store.entity_events(session_id, after["incidents"][0]["incident_id"], after["event_sequence"])
    recorded = next(e for e in events if e.event_type == "INCIDENT_DECISION_RECORDED")
    assert recorded.data["estimated_consequence"]["distance_m"] == chosen["edited"]["distance_m"]

    advance(runtime, 2)
    moved = read(runtime)["vehicles"][0]["position"]
    assert moved != vehicle["position"]
    restored = TrainingSessionService._restore_session(*store.runtime(session_id))
    assert restored.actor.generate_snapshot().vehicles == store.snapshot(session_id).vehicles

    # The route page's report follows the shuttle onto its new route.
    outcome = client.get(f"/api/v2/sessions/{session_id}/routes/{vehicle['route_id']}/outcome").json()
    assert outcome["state"] == "RUNNING" and outcome["vehicle_id"] == vehicle["vehicle_id"]


@pytest.mark.parametrize("rewrite", ["closed", "not-from-here", "not-a-list"])
def test_a_drafted_detour_is_rechecked_when_it_is_approved(runtime, rewrite):
    before = held_at_a_closure(runtime)
    route = next(r for r in before["routes"] if r["route_id"] == before["vehicles"][0]["route_id"])
    suggested = draft_detour(runtime, before)["directed_edge_ids"]
    edges = {"closed": route["directed_edge_ids"], "not-from-here": suggested[1:], "not-a-list": "main:0:forward"}[rewrite]
    result = reroute(runtime, before, edges)
    assert result["status"] == "REJECTED"
    after = read(runtime)
    for field in ("vehicles", "routes", "incidents", "candidates"):
        assert after[field] == before[field]


def test_every_replaced_detour_leaves_the_checkpoint_bounded(runtime):
    client, store, service, session_id = runtime
    before = held_at_a_closure(runtime)
    assert reroute(runtime, before, draft_detour(runtime, before)["directed_edge_ids"])["status"] == "APPLIED"
    actor = service.sessions[session_id].actor
    first = read(runtime)["vehicles"][0]["route_id"]
    assert first in actor._routes

    def close_ahead(actor):
        # The first detour is finished: the actor picks another closure ahead of the shuttle.
        actor.domain.incidents[0].state = "RESOLVED"
        actor.modes[0] = "IN_SERVICE"
        actor.random_incidents = []
        actor.random_incident = None
        actor.incident_due_tick = actor.current_tick

    stage_boundary(runtime, close_ahead)
    advance(runtime, 4)
    held = read(runtime)
    assert held["incidents"][-1]["state"] == "OPTIONS_READY"
    # The editor follows the shuttle onto its detour, whose id is not a fixture route.
    assert client.get(f"/api/v2/sessions/{session_id}/routes/{first}/editor").status_code == 200
    draft = draft_detour(runtime, held)
    assert draft["approvable"], draft["checks"]
    assert reroute(runtime, held, draft["directed_edge_ids"])["status"] == "APPLIED"
    actor = service.sessions[session_id].actor
    assert first not in actor._routes
    assert read(runtime)["vehicles"][0]["route_id"] in actor._routes
    restored = TrainingSessionService._restore_session(*store.runtime(session_id))
    assert restored.actor.generate_snapshot().vehicles == store.snapshot(session_id).vehicles


def test_options_stay_open_for_as_long_as_the_shuttle_is_held(runtime):
    client, store, service, session_id = runtime
    before = held_at_a_closure(runtime)
    lapse = before["candidates"][0]["expires_at_tick"]-before["tick"]+2
    advance(runtime, lapse)
    later = read(runtime)
    assert later["incidents"][0]["state"] == "OPTIONS_READY"
    assert later["incidents"][0]["aggregate_revision"] > before["incidents"][0]["aggregate_revision"]
    assert all(c["expires_at_tick"] >= later["tick"] for c in later["candidates"])
    assert reroute(runtime, later, draft_detour(runtime, later)["directed_edge_ids"])["status"] == "APPLIED"


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
    # Its template's duration, not the scripted opener's
    template = actor.incident_catalog.template(actor.random_incident["kind"])
    assert actor.random_incident["clear_after_ticks"] == round(template.duration_s/actor.STEP_S)


# ---------------------------------------------------------------- incidents as world events (redesign phase 1)

def running_actor(seed, warm_s=20.0):
    """An integrated actor started without the HTTP layer, run until every shuttle is on its route."""
    actor = IntegratedSessionActor(seed, session_id=f"phase-1-{seed}")
    snapshot = actor.generate_snapshot()
    snapshot.session.status = "RUNNING"
    actor.synchronize(snapshot)
    actor.incident_due_tick = 10**12
    for _ in range(round(warm_s/actor.STEP_S)):
        actor.tick()
    return actor


def schedule(actor, count):
    """Places up to count incidents on consecutive ticks, each with every shuttle's target view just
    before it was placed (scheduling runs before the shuttles move)."""
    placed = []
    for _ in range(count):
        before = {s.ID: actor._incident_target(s, set()) for s in actor.shuttles}
        actor.incident_due_tick = actor.current_tick + 1
        known = len(actor.random_incidents)
        actor.tick()
        if len(actor.random_incidents) > known:
            placed.append((actor.random_incidents[-1], before))
    actor.incident_due_tick = 10**12
    return placed


@pytest.mark.random_opening
def test_a_new_session_does_not_open_with_the_scripted_road_block():
    catalog = get_workflow_definition().incidents.simulated_catalog
    assert catalog.opening is None
    for seed in range(5):
        actor = IntegratedSessionActor(seed, session_id=f"opening-{seed}")
        # The first incident comes after an ordinary interval, not 10-11 s in
        assert actor.incident_due_tick*actor.STEP_S >= catalog.policy["min_interval_s"]


@pytest.mark.random_opening
def test_incidents_are_placed_for_any_shuttle_and_only_editable_routes_are_held():
    edges = get_workflow_definition().network["edges"]
    placed = []
    for seed in (3, 11):
        actor = running_actor(seed)
        for selection, before in schedule(actor, 30):
            placed.append(selection)
            target = next(t for t in before.values() if t and t["vehicle"].vehicle_id == selection["vehicle_id"])
            # Every incident is on a road of the operating network, named in its marker
            edge = selection["affected_edge_ids"][0]
            assert edge in edges and selection["map_marker"]["edge_id"] == edge
            assert set(selection["impact_edge_ids"]) <= set(target["remaining"])
            if selection["requires_guidance"]:
                # Only a road block on the remaining route of a shuttle the route editor can take round it
                assert selection["kind"] == "ROAD_BLOCK" and target["editable"]
                assert edge in target["remaining"] and selection["speed_factor"] == 1.0
            elif selection["kind"] == "ROAD_BLOCK":
                # A road block nobody decides on stands on no road a shuttle drives through
                for shuttle in actor.shuttles:
                    assert edge not in {e for e, _, _ in actor._route_roads(shuttle.route_ID)}
            if selection["warning_m"] is not None and selection["kind"] == "ROAD_ACCIDENT":
                # About warning_s at cruising speed ahead: within the window when the route has a road there
                warning = 60.0*target["shuttle"].cruise_vel
                if any(0.5*warning <= d <= 1.5*warning for _, d in target["ahead"]):
                    assert 0.5*warning - 0.1 <= selection["warning_m"] <= 1.5*warning + 0.1
    vehicles = {s["vehicle_id"] for s in placed}
    assert len(vehicles) >= 3 and vehicles - {"shuttle-01"}
    # The peer loops are not drawn on the network, so the route editor cannot take them round a closure
    assert all(s["vehicle_id"] == "shuttle-01" for s in placed if s["requires_guidance"])


def test_peer_loops_follow_roads_of_the_operating_network():
    actor = running_actor(1, warm_s=0.0)
    for shuttle in actor.shuttles[1:]:
        spans = actor._route_roads(shuttle.route_ID)
        length = actor._routes[shuttle.route_ID]["total_length"]
        assert sum(end - start for _, start, end in spans) >= 0.9*length
        assert all(a[2] <= b[1] + 1.0 for a, b in zip(spans, spans[1:]))


def test_congestion_slows_every_shuttle_on_its_roads_whichever_shuttle_it_was_placed_for():
    actor = running_actor(1)
    peer = next(s for s in actor.shuttles[1:] if actor._road_at(s.route_ID, s.progress) is not None)
    road = actor._road_at(peer.route_ID, peer.progress)
    accident = dict(incident_id="random-elsewhere", kind="ROAD_ACCIDENT", vehicle_id="shuttle-01",
                    requires_guidance=False, speed_factor=0.5, affected_edge_ids=[road], impact_edge_ids=[],
                    congested_edge_ids=sorted(actor.definition.neighbours(road)),
                    trigger_tick=actor.current_tick, clear_after_ticks=100)
    actor.random_incidents = [accident]
    assert actor._congestion_factor(peer) == 0.5
    # A road block that holds its shuttle for a decision slows no one else
    actor.random_incidents = [dict(accident, requires_guidance=True, speed_factor=1.0)]
    assert actor._congestion_factor(peer) == 1.0
    # Nor does an incident that has cleared
    actor.random_incidents = [dict(accident, trigger_tick=actor.current_tick - 200)]
    assert actor._congestion_factor(peer) == 1.0


def test_each_incident_is_configured_from_its_own_record():
    service = get_workflow_definition().incidents
    config = service.instance({"incident_id": "random-x", "vehicle_id": "shuttle-03", "kind": "ROAD_ACCIDENT"}).config
    # The scenario's shared rules, and nothing of its authored incident
    assert config["rubric"] == service.config["rubric"] and config["step_s"] == service.config["step_s"]
    assert config["vehicle_id"] == "shuttle-03" and config["trigger_tick"] == NEVER
    assert not config["block_current_route_entrance"]
    assert "route_id" not in config and "disruption_id" not in config
    with pytest.raises(ValueError):
        service.instance({"incident_id": "random-x", "rubric": {"safety_compliance": 100}})


def test_an_alert_names_the_shuttle_the_incident_was_placed_for(runtime):
    client, store, service, session_id = runtime
    approve(runtime)
    send(runtime, "START")

    def seed(actor):
        peer = actor.shuttles[2]
        road = actor._route_roads(peer.route_ID)[-1][0]
        selection = dict(incident_id="random-peer", disruption_id="random-peer-disruption", kind="ROAD_ACCIDENT",
                         vehicle_id="shuttle-03", route_id=actor.domain.vehicles[2].route_id,
                         road_name="Lorong 6 Toa Payoh", affected_edge_ids=[road], impact_edge_ids=[road],
                         congested_edge_ids=[road], requires_guidance=False, speed_factor=0.5,
                         trigger_tick=actor.current_tick+2, clear_after_ticks=10)
        actor.incident_due_tick = 10**9
        actor.random_incidents = [selection]
        actor.random_incident = selection

    stage_boundary(runtime, seed)
    detected = store.snapshot(session_id)
    incident = next(i for i in detected.incidents if i.incident_id == "random-peer")
    assert incident.vehicle_id == "shuttle-03" and incident.state == "DETECTED"
    event = next(e for e in store.entity_events(session_id, "random-peer", detected.event_sequence)
                 if e.event_type == "INCIDENT_DETECTED")
    assert event.data["operational_impact"] == "Potential traffic delay on Shuttle 03's route at Lorong 6 Toa Payoh"
    assert vehicle_label("shuttle-03") == "Shuttle 03"
