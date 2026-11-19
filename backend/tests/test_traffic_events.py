"""Phase 3 of the incident redesign: world events played out by the background traffic.

A breakdown or a collision pins traffic vehicles in their lane and the queue behind them forms of its own
accord; a signal fault sticks a junction's lights on red for the traffic, the shuttles and the signal state
alike; a guidance incident no longer freezes its shuttle in a session with traffic, where the closure itself
stops it. All of it is checkpointed with the traffic, so a session recovered mid-incident drives on exactly.
"""

import asyncio
import json
import math
from copy import deepcopy
from types import SimpleNamespace

import pytest

from backend.tests.test_city_traffic import (
    DT, EAST, _manual_clock, _start, car, road, run, shuttle_on, traffic_on, vehicle,
)
from backend.tests.test_random_incidents import running_actor
from backend.tests.test_training_integration import advance, advance_until, approve, read, runtime, send
from contracts.v2.models import Disruption, Provenance
from src.persistence.training_store import TrainingStore
from src.services.incident_scenarios.world_events import ScheduledEvent, load_timeline
from src.services.integrated_actor import SHUTTLE_FRONT_M, IntegratedSessionActor, get_workflow_definition
from src.services.training_sessions import TrainingSessionService, checkpoint
from src.traffic import city_traffic, incident_hazards, route_signals, signal_cycle
from src.traffic.city_traffic import CityTraffic, ShuttleRoute

# Every test here runs on both traffic engines, which must drive identically (conftest.traffic_engine)
pytestmark = pytest.mark.usefixtures("traffic_engine")

# Two lanes each way of the east road, so one lane can be blocked while the other passes
EAST_TWO = [road("east", [(0.0, 0.0), (600.0, 0.0)], 1, 2, lanes=2), road("on", [(600.0, 0.0), (900.0, 0.0)], 2, 3, lanes=2)]


def east(traffic):
    return traffic.network.by_key["east|1"]


# ---------------------------------------------------------------- pinned vehicles

def test_a_breakdown_pins_a_vehicle_and_its_lane_queues_behind_it_of_its_own_accord():
    traffic = traffic_on(EAST, [car("broken", "east", 110.0, speed_kph=50), car("follower", "east", 20.0, speed_kph=50)])
    assert traffic.pin_vehicle("stall", east(traffic), 120.0, 0, until_s=40.0)
    broken = vehicle(traffic, "broken")
    # The vehicle of its lane nearest the place stops there, dead
    assert broken.distance == pytest.approx(120.0) and broken.speed == 0.0
    clock = run(traffic, 35)
    follower = vehicle(traffic, "follower")
    assert broken.distance == pytest.approx(120.0) and not broken.outside and broken.speed == 0.0
    gap = (broken.distance - broken.half) - (follower.distance + follower.half)
    assert follower.speed < 0.3 and 0.0 < gap <= city_traffic.MIN_GAP_M + 1.0
    assert traffic.report()["overlaps"] == 0
    # When it clears it is towed away, and the queue drives on
    run(traffic, 10, clock)
    assert "stall" not in traffic.pins and broken.slot not in traffic._pinned
    assert broken.outside or broken.distance < 120.0
    assert follower.road.road_id == "on" or follower.distance > 125.0


def test_other_lanes_pass_a_pinned_vehicle():
    traffic = traffic_on(EAST_TWO, [car("broken", "east", 110.0, lane=1), car("passer", "east", 20.0, speed_kph=50)])
    assert traffic.pin_vehicle("stall", east(traffic), 120.0, 1, until_s=100.0)
    run(traffic, 30)
    passer = vehicle(traffic, "passer")
    assert passer.lane == 0 and (passer.road.road_id == "on" or passer.distance > 130.0)
    assert vehicle(traffic, "broken").distance == pytest.approx(120.0)
    assert traffic.report()["overlaps"] == 0


def test_a_collision_pins_two_vehicles_nose_to_tail_and_brings_one_in_from_outside_if_need_be():
    traffic = traffic_on(EAST, [car("first", "east", 300.0), car("far", "east", 20.0)])
    assert traffic.pin_vehicle("crash", east(traffic), 300.0, 0, until_s=60.0, count=2)
    slots, until = traffic.pins["crash"]
    assert until == 60.0 and len(slots) == 2
    first, second = (traffic.vehicles[slot] for slot in slots)
    assert first.id == "first" and first.distance == pytest.approx(300.0)
    # None other is near: the next free vehicle is brought to stand right behind the first
    assert second.id == "far" and second.distance == pytest.approx(300.0 - first.half - city_traffic.PIN_TOUCH_M - second.half)
    # Pinning it again changes nothing
    assert traffic.pin_vehicle("crash", east(traffic), 100.0, 0, until_s=10.0) and traffic.pins["crash"] == (slots, 60.0)


def test_a_pin_waits_while_a_shuttle_stands_where_the_vehicle_would_go():
    traffic = traffic_on(EAST, [car("broken", "east", 110.0)])
    shuttle_on(traffic, ShuttleRoute([(0.0, 0.0), (600.0, 0.0)]), 120.0)
    assert not traffic.pin_vehicle("stall", east(traffic), 120.0, 0, until_s=40.0)
    assert traffic.pins == {} and vehicle(traffic, "broken").distance == pytest.approx(110.0)


def test_a_shuttle_waits_behind_a_pinned_vehicle_it_is_not_let_clear_as_a_jam():
    traffic = traffic_on(EAST, [car("broken", "east", 60.0)])
    assert traffic.pin_vehicle("stall", east(traffic), 60.0, 0, until_s=200.0)
    route = ShuttleRoute([(0.0, 0.0), (600.0, 0.0)])
    # Standing right behind it
    record = shuttle_on(traffic, route, 48.0)
    waited = 0.0
    for i in range(int(60 / DT)):
        plan = traffic.plan_shuttle(record, route, 48.0, 10.0)
        traffic.place_shuttle(record, route, 48.0, 0.0, DT, held_by_traffic=plan.held)
        waited = max(waited, record.wait_s)
        traffic.step(DT, (i + 1) * DT)
    broken = vehicle(traffic, "broken")
    # Well past the wait after which a shuttle has what it waits for leave the area, it is still there
    assert waited >= city_traffic.SHUTTLE_GIVE_UP_S - DT
    assert not broken.outside and broken.distance == pytest.approx(60.0)


def test_a_shuttle_squeezes_past_a_pinned_vehicle_beside_its_way_at_a_crawl():
    route = ShuttleRoute([(0.0, 0.0), (600.0, 0.0)])
    for lane, in_its_way in ((0, False), (1, True)):
        traffic = traffic_on(EAST_TWO, [car("broken", "east", 80.0, lane=lane)])
        assert traffic.pin_vehicle("stall", east(traffic), 80.0, lane, until_s=100.0)
        record = shuttle_on(traffic, route, 20.0, speed=10.0)
        # The shuttle keeps to the kerb lane, the left one
        assert record.aside == pytest.approx(east(traffic).lane_offset(1))
        plan = traffic.plan_shuttle(record, route, 20.0, 10.0)
        broken = vehicle(traffic, "broken")
        if in_its_way:
            # In its lane: it stops behind it, as for any standing vehicle
            assert broken.slot in record.gives_way_to
            assert plan.cap_m == pytest.approx(80.0 - broken.half - 20.0 - SHUTTLE_FRONT_M - city_traffic.MIN_GAP_M, abs=0.01)
        else:
            assert not plan.held and broken.slot not in record.gives_way_to and plan.cap_m == float("inf")
            # Braking in time to be at a crawl when it gets there
            to_it = 80.0 - 20.0 - broken.half - SHUTTLE_FRONT_M
            assert plan.limit_mps == pytest.approx(math.sqrt(city_traffic.PINNED_PASS_MPS ** 2
                                                             + 2 * city_traffic.COMFORT_BRAKE * to_it))
            alongside = traffic.plan_shuttle(record, route, 80.0 - broken.half - SHUTTLE_FRONT_M, 10.0)
            assert alongside.limit_mps == pytest.approx(city_traffic.PINNED_PASS_MPS)


def test_pins_and_faults_survive_a_checkpoint_exactly_and_a_staged_copy_leaves_them_alone():
    junction = {"id": "j", "offset_s": 0, "approaches": [
        {"road": "east", "heading_deg": 0.0, "stop_x_m": 300.0, "stop_y_m": 0.0, "phase": 0}]}
    traffic = traffic_on(EAST_TWO, [car(f"c{i}", "east", 20.0 * i, speed_kph=40, lane=i % 2) for i in range(1, 12)],
                         [junction])
    assert traffic.pin_vehicle("crash", east(traffic), 200.0, 1, until_s=30.0, count=2)
    traffic.set_signal_fault("j", 25.0)
    clock = run(traffic, 5)
    state = json.loads(json.dumps(traffic.state()))
    assert state["pins"] and state["signal_faults"] == [["j", 25.0]]
    restored = CityTraffic.from_state(state, traffic.network)
    staged = deepcopy(traffic)
    staged.pin_vehicle("stall", east(staged), 400.0, 0, until_s=50.0)
    staged.set_signal_fault("j", 90.0)
    assert set(traffic.pins) == {"crash"} and traffic.signal_faults == {"j": 25.0}
    run(traffic, 30, clock)
    run(restored, 30, clock)
    assert restored.state() == traffic.state()
    # Both have cleared on the traffic's own clock
    assert traffic.pins == {} and traffic.signal_faults == {}


def test_a_checkpoint_from_before_world_events_has_none():
    traffic = traffic_on(EAST, [car("a", "east", 50.0)])
    state = traffic.state()
    del state["pins"], state["signal_faults"]
    restored = CityTraffic.from_state(state, traffic.network)
    assert restored.pins == {} and restored.signal_faults == {}
    bad = {**traffic.state(), "pins": [["x", [99], 10.0]]}
    with pytest.raises(ValueError):
        CityTraffic.from_state(bad, traffic.network)


def test_locate_finds_the_road_under_an_edge_running_its_way():
    traffic = traffic_on(EAST, [car("a", "east", 50.0)])
    found, along = traffic.locate([(100.0, 0.5), (300.0, 0.5)])
    assert found is east(traffic) and along == pytest.approx(200.0)
    assert traffic.locate([(300.0, 0.0), (100.0, 0.0)]) is None, "the east road is one way"


# ---------------------------------------------------------------- signal faults

FAULTED = {"id": "j", "offset_s": 0, "approaches": [
    {"road": "east", "heading_deg": 0.0, "stop_x_m": 300.0, "stop_y_m": 0.0, "phase": 0}]}


def test_a_faulted_junction_holds_its_approach_through_its_green_until_it_clears():
    traffic = traffic_on(EAST, [car("driver", "east", 100.0, speed_kph=80)], [FAULTED])
    traffic.set_signal_fault("j", 75.0)
    clock = run(traffic, 72)
    driver = vehicle(traffic, "driver")
    # Phase 0 would be green again from 70 s; stuck on red, the light still holds it
    assert signal_cycle.phase_state(clock, 0, 0)["color"] == "green"
    assert driver.speed < 0.3 and 290.0 < driver.distance + driver.half < 300.0 and not driver.outside
    run(traffic, 12, clock)
    assert driver.road.road_id == "on" or driver.distance > 300.0, "on once the fault has cleared"
    assert traffic.red_light_runs == 0 and traffic.signal_faults == {}


def test_the_lights_the_shuttles_and_the_signal_state_share_one_fault():
    faults = {"j": 75.0}
    assert city_traffic.light(10.0, 0, 0) == 0 and city_traffic.light(10.0, 0, 0, 75.0) == 2
    assert city_traffic.light(80.0, 0, 0, 75.0) == 0
    state = signal_cycle.phase_state(10.0, 0, 0, signal_cycle.fault_until(faults, "j", 10.0))
    assert state == {"phase": 0, "color": "red", "seconds_left": 65.0}
    states = signal_cycle.junction_states([{"id": "j", "offset_s": 0}, {"id": "k", "offset_s": 0}], 10.0, faults)
    assert [p["color"] for p in states[0]["phases"]] == ["red", "red"]
    assert states[0]["fault"] == {"mode": "STUCK_RED", "until_s": 75.0} and "fault" not in states[1]
    assert states[1]["phases"][0]["color"] == "green"
    line = route_signals.StopLine(100.0, 0, 0, "j")
    assert not route_signals.must_stop(line, 10.0, 50.0, 10.0)
    assert route_signals.must_stop(line, 10.0, 50.0, 10.0, faults)
    assert route_signals.hold_point([line], 10.0, 20.0, 6.6, 10.0, faults=faults) == pytest.approx(100.0 - 6.6 - 0.5)
    assert route_signals.hold_point([line], 10.0, 20.0, 6.6, 10.0) is None
    assert route_signals.hold_point([line], 80.0, 20.0, 6.6, 10.0, faults=faults) is None


def test_a_signal_fault_footprint_is_the_roads_approaching_its_stop_lines():
    definition = get_workflow_definition()
    edges = definition.network["edges"]
    for junction_id in list(definition.signal_junctions)[:10]:
        for edge_id, low, high in definition.signal_footprint(junction_id):
            assert high >= incident_hazards.LEAVES_LINE_M and 0.0 <= low < high
            assert high - low <= incident_hazards.REACH_M + 1e-9
            assert edge_id in edges


def test_a_timeline_schedules_a_signal_fault_at_a_junction_only():
    junctions = set(get_workflow_definition().signal_junctions)
    row = {"event_id": "lights", "event_type": "SIGNAL_FAULT", "location": {"node_id": "junction-028"},
           "at_s": 5, "duration_s": 60}
    event, = load_timeline([row], junctions=junctions)
    assert (event.node_id, event.edge_id) == ("junction-028", None)
    placed = event.place(0.05)
    assert placed.location == {"node_id": "junction-028"} and placed.kind == "SIGNAL_FAULT"
    for bad in ({**row, "location": {"edge_id": "main:0:forward"}}, {**row, "location": {"node_id": "nowhere"}}):
        with pytest.raises(ValueError):
            load_timeline([bad], junctions=junctions)


# ---------------------------------------------------------------- what the traffic keeps off

def record(event_type, *, start=100, trigger=140, clear=600, edge="main:0:forward", disruption="d-1"):
    kind = {"ROAD_CLOSURE": "ROAD_BLOCK", "SIGNAL_FAULT": "SIGNAL_FAULT"}.get(event_type, "ROAD_ACCIDENT")
    return {"incident_id": f"x-{event_type}", "kind": kind, "disruption_id": disruption, "trigger_tick": trigger,
            "clear_after_ticks": clear, "map_marker": {"edge_id": edge},
            "event": {"event_type": event_type, "start_tick": start}}


def test_a_scene_is_kept_off_from_when_its_event_starts_until_it_clears_and_has_no_drawn_queue():
    edges = get_workflow_definition().network["edges"]
    snapshot = SimpleNamespace(disruptions=[])
    collision = record("COLLISION")
    assert incident_hazards.live_hazards([collision], snapshot, edges, 99) == []
    # Before it is detected (tick 140), the traffic already keeps off the scene, both ways, and only it
    scene, = incident_hazards.live_hazards([collision], snapshot, edges, 120)
    assert scene[1] is True
    assert incident_hazards.live_hazards([collision], snapshot, edges, 740) == []
    # A breakdown has only its pinned vehicle, and a signal fault only its lights
    assert incident_hazards.live_hazards([record("BREAKDOWN"), record("SIGNAL_FAULT", edge=None)], snapshot, edges, 120) == []
    closed, = incident_hazards.live_hazards([record("ROAD_CLOSURE")], snapshot, edges, 120)
    assert closed[0] == [tuple(p) for p in edges["main:0:forward"]["path"]]


def test_an_escalated_closure_clears_when_its_disruption_ends():
    closure = record("ROAD_CLOSURE")
    source = Provenance(kind="TRAINING_SIMULATION", authored=True)
    shortened = Disruption(disruption_id="d-1", aggregate_revision=2, affected_edge_ids=["main:0:forward"],
                           active_from_tick=140, active_until_tick=300, reason="Escalated", provenance=source)
    assert incident_hazards.event_window(closure, []) == (100, 740)
    assert incident_hazards.event_window(closure, [shortened]) == (100, 300)


# ---------------------------------------------------------------- the session plays events out

def ahead_of(actor, shuttle_index, low_m, high_m, *, avoid=()):
    """An operating-network edge on a shuttle's route starting between low_m and high_m ahead of it, with a
    road of the traffic under it."""
    shuttle = actor.shuttles[shuttle_index]
    edges = actor.definition.network["edges"]
    for edge, start, _ in actor._route_roads(shuttle.route_ID):
        if (edge in edges and edge not in avoid and low_m <= start - shuttle.progress <= high_m
                and actor.traffic.locate([tuple(p) for p in edges[edge]["path"]]) is not None):
            return edge
    raise AssertionError("No edge of the traffic ahead of the shuttle")


def stop_line_ahead(actor, low_m, high_m):
    """A shuttle on its service route with a light's stop line between low_m and high_m ahead of it, and
    that line."""
    for index, shuttle in enumerate(actor.shuttles):
        if shuttle.route_ID != actor.domain.vehicles[index].route_id:
            continue
        for line in actor._route_stop_lines(shuttle.route_ID):
            if low_m <= line.progress_m - shuttle.progress <= high_m:
                return index, line
    raise AssertionError("No shuttle has a light ahead")


@pytest.mark.traffic
def test_scheduled_events_play_out_in_the_traffic_session(monkeypatch):
    actor = running_actor(5, warm_s=5.0)
    assert actor.traffic is not None
    now_s = actor.current_tick * actor.STEP_S
    stall = ahead_of(actor, 1, 60.0, 400.0)
    held, line = stop_line_ahead(actor, 30.0, 300.0)
    timeline = [ScheduledEvent("stall", "BREAKDOWN", stall, at_s=now_s + 0.5, duration_s=8.0),
                ScheduledEvent("lights", "SIGNAL_FAULT", None, at_s=now_s + 0.5, duration_s=12.0,
                               node_id=line.junction_id)]
    monkeypatch.setattr(actor.incident_catalog, "timeline", timeline)
    pinned_at, held_back = None, True
    for _ in range(round(14.0 / actor.STEP_S)):
        actor.tick()
        pins = actor.traffic.pins
        if "stall" in pins:
            broken = actor.traffic.vehicles[pins["stall"][0][0]]
            where = (broken.road.index, broken.lane, broken.distance)
            assert pinned_at in (None, where) and broken.speed == 0.0
            pinned_at = where
        faulted = "lights" in {r["incident_id"].removeprefix("scheduled-") for r, _ in actor._live_events(actor.current_tick)}
        if faulted:
            assert line.junction_id in actor.traffic.signal_faults or actor.current_tick % 2
            state = actor.signal_state()
            junction = next(j for j in state["junctions"] if j["id"] == line.junction_id)
            assert junction["fault"]["mode"] == "STUCK_RED"
            # The shuttle never runs the faulted junction's line
            shuttle = actor.shuttles[held]
            if shuttle.route_ID == actor.domain.vehicles[held].route_id:
                held_back &= shuttle.progress + SHUTTLE_FRONT_M <= line.progress_m + 1e-6 or \
                    shuttle.progress + SHUTTLE_FRONT_M > line.progress_m + 50.0
    records = {r["incident_id"]: r for r in actor.random_incidents}
    assert records["scheduled-stall"]["holds_vehicle"] is False
    assert records["scheduled-lights"]["kind"] == "SIGNAL_FAULT"
    assert records["scheduled-lights"]["road_name"].startswith("the junction of ")
    assert pinned_at is not None and held_back
    # Both have cleared: the broken-down vehicle is towed away and the lights work again
    assert "stall" not in actor.traffic.pins and line.junction_id not in actor.traffic.signal_faults
    assert actor._congestion_factor(actor.shuttles[1]) == 1.0
    frame = actor.traffic_frame()
    assert frame["signal_faults"] == []


@pytest.mark.traffic
@pytest.mark.random_opening
def test_a_guidance_closure_does_not_freeze_its_shuttle_in_traffic_the_closure_stops_it(runtime, monkeypatch):
    client, store, service, session_id = runtime
    approve(runtime)
    actor = service.sessions[session_id].actor
    # A closure well up shuttle-01's approved route, which it is asked about
    closed = ahead_of(actor, 0, 80.0, 250.0)
    monkeypatch.setattr(actor.incident_catalog, "timeline",
                        [ScheduledEvent("closure", "ROAD_CLOSURE", closed, at_s=3.0, duration_s=600.0)])
    send(runtime, "START")
    advance_until(runtime, lambda s: any(i.incident_id == "scheduled-closure" for i in s.incidents))

    def shuttle():
        # Each step installs a staged copy of the actor
        return service.sessions[session_id].actor.shuttles[0]
    actor = service.sessions[session_id].actor
    record = actor.random_incident
    assert record["requires_guidance"] and record["vehicle_id"] == "shuttle-01" and record["holds_vehicle"] is False
    start = next(s for e, s, _ in actor._route_roads(actor.shuttles[0].route_ID) if e == closed)
    detected_at = shuttle().progress
    stopped = False
    for _ in range(90):
        advance(runtime, 20)
        snapshot = read(runtime)
        assert snapshot["vehicles"][0]["service_state"] != "AWAITING_GUIDANCE"
        # It never drives onto the closed road
        assert shuttle().progress + SHUTTLE_FRONT_M <= start + 1e-6
        # It drives on to the closure and stands there, or behind the traffic already queued at it
        if shuttle().progress > detected_at + 20.0 and shuttle().curr_vel == 0.0:
            stopped = True
            break
    assert stopped, "it drove on towards the closure, not held where it was told"
    # A decision does not need it stopped where it was told: escalating brings help, and it drives on
    incident = next(i for i in snapshot["incidents"] if i["incident_id"] == "scheduled-closure")
    candidate = next(c for c in snapshot["candidates"] if c["action"] == "ESCALATE")
    result = send(runtime, "APPROVE_INCIDENT_OPTION", snapshot=snapshot, target_type="INCIDENT",
                  target_id=incident["incident_id"], revision=incident["aggregate_revision"],
                  payload={"candidate_id": candidate["candidate_id"]})
    assert result["status"] == "APPLIED", result
    advance(runtime, 800)
    assert shuttle().progress + SHUTTLE_FRONT_M > start, "on through once the closure cleared"


@pytest.mark.traffic
def test_a_session_recovered_mid_incident_drives_its_events_on_exactly(tmp_path, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", _manual_clock)
    probe = IntegratedSessionActor(1000, session_id="probe")
    crash = ahead_of(probe, 1, 80.0, 500.0)
    stall = ahead_of(probe, 3, 80.0, 500.0, avoid={crash})
    junction = stop_line_ahead(probe, 0.0, 2000.0)[1].junction_id
    timeline = [ScheduledEvent("crash", "COLLISION", crash, at_s=2.0, duration_s=60.0),
                ScheduledEvent("stall", "BREAKDOWN", stall, at_s=3.0, duration_s=10.0),
                ScheduledEvent("lights", "SIGNAL_FAULT", None, at_s=4.0, duration_s=11.0, node_id=junction)]
    monkeypatch.setattr(get_workflow_definition().incidents.simulated_catalog, "timeline", timeline)

    async def exercise(path, *, recover_at=None, ticks=400):
        actor = IntegratedSessionActor(1000, session_id="events-recovery")
        mid = None
        with TrainingStore(path) as store:
            store.create_session(actor.generate_snapshot(), runtime_state=checkpoint(actor))
            service = TrainingSessionService(store)
            await service.initialize()
            await _start(service, store, actor)
            for tick in range(ticks):
                if tick == recover_at:
                    await service.close()
                    service = TrainingSessionService(store)
                    await service.initialize()
                await service.step(service.sessions[actor.session_id])
                live = service.sessions[actor.session_id].actor
                if live.current_tick == 220:
                    mid = (dict(live.traffic.pins), dict(live.traffic.signal_faults))
            live = service.sessions[actor.session_id].actor
            frames = service.traffic(actor.session_id, live.current_tick - 6)
            result = (live.current_tick, live.traffic.state(), [s.model_dump(mode="json") for s in live.shuttles])
            await service.close()
            return result, frames, mid

    (tick, traffic, shuttles), frames, mid = asyncio.run(exercise(tmp_path / "straight.sqlite"))
    (tick_r, traffic_r, shuttles_r), frames_r, mid_r = asyncio.run(exercise(tmp_path / "recovered.sqlite", recover_at=200))
    # Recovered while every event was on the road: the crash's two vehicles, the stall's one and the lights
    pins, faults = mid
    counts = {key: len(slots) for key, (slots, _) in pins.items()}
    assert counts["crash"] == 2 and counts["stall"] == 1
    assert set(faults) == {junction}
    assert mid_r == mid
    # The stall and the fault cleared after the recovery, the crash had not: in both the same
    assert tick == tick_r == 400
    keys = [key for key, _, _ in traffic["pins"]]
    assert "crash" in keys and "stall" not in keys and traffic["signal_faults"] == []
    assert traffic_r == traffic and shuttles_r == shuttles
    assert len(frames) == 3 and frames_r == frames
