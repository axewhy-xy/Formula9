"""Phase 2 of the incident redesign: world events, the scenario timeline, exposure and checkpoint v4."""

from copy import deepcopy

import pytest
from pydantic import ValidationError

from backend.tests.test_random_incidents import running_actor, schedule
from backend.tests.test_training_integration import advance, approve, read, runtime, send, stage_boundary
from contracts.v2.models import parse_message
from src.services.incident_scenarios.world_events import ScheduledEvent, WorldEvent, legacy_event, load_timeline
from src.services.incidents import exposure_headline
from src.services.integrated_actor import get_workflow_definition, upgrade_v3_workflow
from src.services.training_sessions import TrainingSessionService, checkpoint
from src.traffic import incident_hazards
from backend.tests.test_training_contract_v2 import FIXTURES

import json


def row(**changes):
    return {"event_id": "crash-1", "event_type": "COLLISION", "location": {"edge_id": "main:0:forward"},
            "at_s": 30, "duration_s": 120, **changes}


def test_a_timeline_loads_sorted_and_takes_missing_timings_from_the_template():
    timeline = load_timeline([row(event_id="late", at_s=90), row(event_id="early", at_s=10, detection_delay_s=4)],
                             edges={"main:0:forward"})
    assert [e.event_id for e in timeline] == ["early", "late"]
    template = get_workflow_definition().incidents.simulated_catalog.template("ROAD_ACCIDENT")
    event = timeline[0].place(0.05, template)
    assert event.start_tick == 200 and event.detection_delay_s == 4 and event.warning_s == template.warning_s
    assert event.detected_tick(0.05) == 280 and event.end_tick(0.05) == 200 + 120*20
    assert event.kind == "ROAD_ACCIDENT" and event.location == {"edge_id": "main:0:forward"}


@pytest.mark.parametrize("bad", [
    row(event_type="SIGNAL_FAULT"), row(event_type="METEOR"), row(location={"node_id": "4"}),
    row(location={"edge_id": "nowhere"}), row(duration_s=0), row(at_s=-1), row(detection_delay_s=120),
    row(extra=True), row(event_id="bad id"),
])
def test_a_bad_timeline_fails_at_load(bad):
    with pytest.raises(ValueError):
        load_timeline([bad], edges={"main:0:forward"})


def test_duplicate_scheduled_events_fail_at_load():
    with pytest.raises(ValueError):
        load_timeline([row(), row(at_s=50)])


def test_a_world_event_is_at_exactly_one_place_and_detected_before_it_ends():
    with pytest.raises(ValueError):
        WorldEvent("x", "COLLISION", "e", "4", 0, 10.0)
    with pytest.raises(ValueError):
        WorldEvent("x", "COLLISION", None, None, 0, 10.0)
    with pytest.raises(ValueError):
        WorldEvent("x", "BREAKDOWN", "e", None, 0, 10.0, detection_delay_s=10.0)
    junction = WorldEvent("x", "SIGNAL_FAULT", None, "4", 0, 10.0)
    assert junction.location == {"node_id": "4"} and WorldEvent.from_record(junction.to_record()) == junction


def test_a_v3_record_becomes_the_world_event_it_stood_for():
    record = {"incident_id": "random-road_accident-40", "kind": "ROAD_ACCIDENT", "trigger_tick": 40,
              "clear_after_ticks": 600, "affected_edge_ids": ["main:0:forward"],
              "map_marker": {"edge_id": "main:0:forward"}}
    event = WorldEvent.from_record(legacy_event(record, 0.05))
    assert (event.event_type, event.edge_id, event.start_tick) == ("COLLISION", "main:0:forward", 40)
    assert event.end_tick(0.05) == 640


def test_a_closure_footprint_is_its_road_and_the_queue_feeding_it():
    edges = get_workflow_definition().network["edges"]
    edge_id = "main:0:forward"
    found = incident_hazards.footprint("ROAD_BLOCK", edge_id, edges, set())
    roads = {road for road, _, _ in found}
    assert edge_id in roads and len(roads) > 1
    # The queue backs up every road feeding it (and on up the roads feeding those, within its reach)
    feeders = {key for key, edge in edges.items() if edge["target"] == edges[edge_id]["source"]
               and edge["source"] != edges[edge_id]["target"]}
    assert feeders <= roads
    upstream = {edge["source"] for key, edge in edges.items() if key in roads}
    assert all(edges[road]["target"] in upstream | {edges[edge_id]["source"]} for road in roads - {edge_id})
    for road, low, high in found:
        assert 0 <= low < high
    # An accident's scene covers the road's other direction too
    accident = {road for road, _, _ in incident_hazards.footprint("ROAD_ACCIDENT", edge_id, edges, set())}
    reverse = [k for k, e in edges.items() if e["source"] == edges[edge_id]["target"] and e["target"] == edges[edge_id]["source"]]
    assert set(reverse) <= accident


@pytest.mark.random_opening
def test_random_incidents_carry_their_world_event_and_reach_shuttles_inside_the_warning_window():
    actor = running_actor(3)
    placed = [selection for selection, _ in schedule(actor, 6)]
    assert placed and all(s["event"]["edge_id"] == s["affected_edge_ids"][0] for s in placed)
    assert {s["event"]["event_type"] for s in placed} <= {"COLLISION", "ROAD_CLOSURE"}
    exposed = [e for s in placed for e in s["exposures"]]
    assert exposed, "an incident placed warning_s ahead of a shuttle must reach it while it lasts"
    for selection in placed:
        etas = [e["eta_s"] for e in selection["exposures"]]
        assert etas == sorted(etas)
        window = (selection["clear_after_ticks"])*actor.STEP_S
        # Only the shuttle held for a decision is exposed however long the closure has left
        assert all(0 <= e["eta_s"] <= window for e in selection["exposures"]
                   if not (selection["requires_guidance"] and e["vehicle_id"] == selection["vehicle_id"]))
        if selection["warning_m"] is not None and selection["exposures"]:
            # The shuttle it was placed ahead of reaches it about warning_s after detection
            mine = next((e for e in selection["exposures"] if e["vehicle_id"] == selection["vehicle_id"]), None)
            assert mine is not None and mine["distance_m"] <= selection["warning_m"] + 1.0


@pytest.mark.random_opening
def test_exposure_drops_a_shuttle_once_the_incident_clears_before_it_gets_there():
    actor = running_actor(3)
    selection = next(s for s, _ in schedule(actor, 6) if s["exposures"] and not s["requires_guidance"])
    closed = set()
    assert actor._exposure(selection, closed)[0]
    # The same incident, about to clear: nobody reaches it any more
    short = dict(selection, clear_after_ticks=actor.current_tick - selection["trigger_tick"] + 1)
    assert actor._exposure(short, closed)[0] == []


@pytest.mark.random_opening
def test_detection_can_follow_the_start_and_the_congestion_starts_with_the_event(monkeypatch):
    actor = running_actor(1)
    target = actor._incident_target(actor.shuttles[0], set())
    edge = target["ahead"][0][0]
    catalog = actor.incident_catalog
    at = (actor.current_tick + 1)*actor.STEP_S
    monkeypatch.setattr(catalog, "timeline", [ScheduledEvent("late-report", "COLLISION", edge, at, 200.0,
                                                             detection_delay_s=5.0)])
    actor.tick()
    record = actor.random_incidents[-1]
    assert record["incident_id"] == "scheduled-late-report" and record["vehicle_id"] is None
    assert record["trigger_tick"] == record["event"]["start_tick"] + 100
    assert record["trigger_tick"] + record["clear_after_ticks"] == record["event"]["start_tick"] + 4000
    # Not detected yet, so not exposed yet; but it already slows the road
    assert "exposures" not in record
    assert actor._incident_window(record)[0] == record["event"]["start_tick"]
    assert actor.timeline_next == 1


def scheduled(runtime, monkeypatch, event_type, *, duration_s=300.0, distance=(150.0, 600.0)):
    """Starts the session, then schedules one timeline event on a road ahead of shuttle-01."""
    client, store, service, session_id = runtime
    approve(runtime)
    send(runtime, "START")
    advance(runtime, 20)
    actor = service.sessions[session_id].actor
    target = actor._incident_target(actor.shuttles[0], set())
    edge = next(e for e, d in target["ahead"] if distance[0] <= d <= distance[1])
    at = (actor.current_tick + 4)*actor.STEP_S
    monkeypatch.setattr(actor.incident_catalog, "timeline",
                        [ScheduledEvent("e1", event_type, edge, at + 2*actor.STEP_S, duration_s)])
    stage_boundary(runtime, lambda a: setattr(a, "incident_due_tick", 10**9))
    advance(runtime, 6)
    return edge


def test_a_scheduled_collision_belongs_to_no_shuttle_and_names_who_reaches_it(runtime, monkeypatch):
    client, store, service, session_id = runtime
    edge = scheduled(runtime, monkeypatch, "COLLISION")
    snapshot = read(runtime)
    incident = next(i for i in snapshot["incidents"] if i["incident_id"] == "scheduled-e1")
    assert incident["vehicle_id"] is None and incident["state"] == "DETECTED"
    assert incident["event_type"] == "COLLISION" and incident["location"]["edge_id"] == edge
    assert "shuttle-01" in incident["exposed_vehicle_ids"]
    assert [e["vehicle_id"] for e in incident["exposures"]] == incident["exposed_vehicle_ids"]
    event = next(e for e in store.entity_events(session_id, "scheduled-e1", snapshot["event_sequence"])
                 if e.event_type == "INCIDENT_DETECTED")
    assert event.data["alert"] is True and event.data["event_type"] == "COLLISION"
    assert " reaches a collision" in event.data["headline"]
    assert edge in event.data["footprint_edge_ids"]
    assert event.data["operational_impact"].startswith("Potential traffic delay for ")
    # Exposure follows the shuttles: as they move their ETAs are refreshed without a new revision
    revision = incident["aggregate_revision"]
    eta = incident["exposures"][0]["eta_s"]
    advance(runtime, 20)
    later = next(i for i in read(runtime)["incidents"] if i["incident_id"] == "scheduled-e1")
    assert later["aggregate_revision"] == revision and later["exposures"][0]["eta_s"] < eta

    # Everyone rerouted round it: the change of who is exposed is an event of its own
    def rerouted(actor):
        for record in actor.random_incidents:
            record["exposures"] = []
    stage_boundary(runtime, rerouted)
    snapshot = read(runtime)
    cleared = next(i for i in snapshot["incidents"] if i["incident_id"] == "scheduled-e1")
    assert cleared["exposed_vehicle_ids"] == [] and cleared["exposures"] == []
    changed = [e for e in store.entity_events(session_id, "scheduled-e1", snapshot["event_sequence"])
               if e.event_type == "INCIDENT_EXPOSURE_CHANGED"]
    assert changed and "shuttle-01" in changed[-1].data["removed_vehicle_ids"]


def test_a_scheduled_closure_ahead_of_an_editable_route_asks_that_shuttle(runtime, monkeypatch):
    client, store, service, session_id = runtime
    scheduled(runtime, monkeypatch, "ROAD_CLOSURE")
    snapshot = read(runtime)
    incident = next(i for i in snapshot["incidents"] if i["incident_id"] == "scheduled-e1")
    assert incident["vehicle_id"] == "shuttle-01" and incident["state"] == "OPTIONS_READY"
    assert incident["event_type"] == "ROAD_CLOSURE" and "shuttle-01" in incident["exposed_vehicle_ids"]
    options = [c for c in snapshot["candidates"] if c["incident_id"] == "scheduled-e1"]
    assert options and all(c["vehicle_id"] == "shuttle-01" for c in options)


def test_checkpoints_are_version_4_and_a_version_3_checkpoint_is_converted(runtime, monkeypatch):
    client, store, service, session_id = runtime
    scheduled(runtime, monkeypatch, "COLLISION")
    snapshot, state = store.runtime(session_id)
    assert state["version"] == 4 and state["workflow"]["timeline_next"] == 1
    assert all("event" in record for record in state["workflow"]["random_incidents"])
    assert checkpoint(service.sessions[session_id].actor, traffic=False)["version"] == 4
    old = deepcopy(state)
    old["version"] = 3
    del old["workflow"]["timeline_next"]
    for record in [*old["workflow"]["random_incidents"], old["workflow"]["random_incident"]]:
        record.pop("event", None)
        record.pop("exposures", None)
        record.pop("footprint_edge_ids", None)
    restored = TrainingSessionService._restore_session(snapshot, old)
    actor = restored.actor
    assert actor.timeline_next == 1  # the timeline up to the session's tick counts as past
    record = next(r for r in actor.random_incidents if r["incident_id"] == "scheduled-e1")
    assert record["event"]["event_type"] == "COLLISION" and record["event"]["start_tick"] == record["trigger_tick"]
    assert actor.generate_snapshot().vehicles == snapshot.vehicles
    # A checkpoint that says where it is in a timeline it cannot have reached is corrupt
    broken = deepcopy(state)
    broken["workflow"]["timeline_next"] = 7
    with pytest.raises(Exception):
        TrainingSessionService._restore_session(snapshot, broken)


def test_upgrading_counts_only_timeline_events_already_due():
    workflow = {"random_incident": None, "random_incidents": []}
    timeline = [ScheduledEvent("a", "COLLISION", "e", 1.0, 10.0), ScheduledEvent("b", "COLLISION", "e", 9.0, 10.0)]
    assert upgrade_v3_workflow(workflow, 100, 0.05, timeline)["timeline_next"] == 1
    assert "timeline_next" not in workflow


def test_alert_headlines_name_who_reaches_it_and_when():
    exposures = [{"vehicle_id": "shuttle-03", "eta_s": 84.6, "distance_m": 800.0},
                 {"vehicle_id": "shuttle-04", "eta_s": 110.0, "distance_m": 1000.0}]
    assert (exposure_headline("COLLISION", "Lorong 6 Toa Payoh", exposures)
            == "Shuttle 03 reaches a collision on Lorong 6 Toa Payoh in about 85 s (and 1 more shuttle)")
    assert exposure_headline("BREAKDOWN", None, []) == "A breakdown; no shuttle reaches it before it clears"


def snapshot_fixture(name="snapshot"):
    return json.loads((FIXTURES / f"{name}.json").read_text(encoding="utf-8"))


def test_the_world_event_fixture_has_an_incident_that_names_no_shuttle():
    message = parse_message(snapshot_fixture("world_event_snapshot"))
    incident = next(i for i in message.incidents if i.incident_id == "scheduled-collision-1")
    assert incident.vehicle_id is None and incident.exposed_vehicle_ids == ["shuttle-03", "shuttle-04"]


@pytest.mark.parametrize("change", [
    lambda s: s["incidents"][0].update(exposed_vehicle_ids=["shuttle-02"]),
    lambda s: s["incidents"][0].update(exposed_vehicle_ids=["shuttle-09"], exposures=[
        {"vehicle_id": "shuttle-09", "distance_m": 1.0, "eta_s": 1.0, "is_estimate": True}]),
    lambda s: s["incidents"][0].update(location={"edge_id": "edge-1", "node_id": "7"}),
    lambda s: s["incidents"][0].update(event_type="METEOR"),
    lambda s: s["candidates"][0].update(vehicle_id="shuttle-09"),
    lambda s: s["incidents"][0]["exposures"][0].update(eta_s=-1.0),
])
def test_world_event_fields_are_validated(change):
    value = snapshot_fixture()
    change(value)
    with pytest.raises(ValidationError):
        parse_message(value)
