"""
Tests for the background traffic the backend drives (backend/src/traffic/road_network.py, city_traffic.py
and incident_hazards.py) and for the integrated session driving its shuttles through it.

Description:
    On small made-up roads: cars follow, stop at a red light and go on green, keep off a hazard, and
    never drive into a shuttle; a shuttle stops behind a standing car and a car queues behind a
    shuttle held in traffic. On the real Toa Payoh sample: the traffic is deterministic and survives a
    checkpoint and a staged copy exactly. In a live session: shuttles queue behind the traffic at red
    lights instead of driving through it, never run a red light or touch a vehicle, a recovered session
    drives on exactly as the original, and the 3D view gets the traffic's frames.
"""

from copy import deepcopy
import asyncio
import base64
import json
import math
import time

import pytest

from contracts.v2.models import DispatcherCommand
from contracts.v2.route_edit import RouteApprovalRequest, RouteEditRequest
from src.services import integrated_actor
from src.services.integrated_actor import SHUTTLE_FRONT_M, IntegratedSessionActor
from src.services import training_sessions
from src.services.training_sessions import TrainingSessionService, checkpoint, next_deadline
from src.persistence.training_store import TrainingStore
from src.traffic import city_traffic, incident_hazards, route_signals, signal_cycle, walk_network
from src.traffic.city_traffic import FRAME_RECORD, CityTraffic, ShuttleRoute, overlap, unpack_frame
from src.traffic.road_network import RoadNetwork

# Every test here runs on both traffic engines, which must drive identically (conftest.traffic_engine)
pytestmark = pytest.mark.usefixtures("traffic_engine")

MODELS = {"car": {"length_m": 4.5, "width_m": 1.8, "height_m": 1.5}}
DT = 0.05


def road(identifier, points, start, end, *, oneway=True, lanes=1, speed_kph=50):
    return {"id": identifier, "class": "primary", "from_node": start, "to_node": end, "oneway": oneway,
            "lanes": lanes, "speed_kph": speed_kph, "points_m": points}


def car(identifier, road_id, distance, *, speed_kph=0, lane=0):
    return {"id": identifier, "model": "car", "road": road_id, "direction": 1, "lane": lane,
            "distance_m": distance, "speed_kph": speed_kph, "color": "#ffffff"}


def traffic_on(roads, cars, junctions=()):
    return CityTraffic.on(RoadNetwork("test", {"models": MODELS, "vehicles": cars, "roads": roads}, tuple(junctions)))


# A straight 600 m one-way road east, and its continuation, so nothing leaves the area
EAST = [road("east", [(0.0, 0.0), (600.0, 0.0)], 1, 2), road("on", [(600.0, 0.0), (900.0, 0.0)], 2, 3)]


def run(traffic, seconds, start_s=0.0):
    for i in range(round(seconds / DT)):
        traffic.step(DT, start_s + (i + 1) * DT)
    return start_s + seconds


def vehicle(traffic, identifier):
    return next(v for v in traffic.vehicles if v.id == identifier)


def closed_beyond(traffic, start_m):
    """Closes the east road from start_m on, the way it runs, so a car stands short of it."""
    traffic.set_hazards([([(start_m, 0.0), (600.0, 0.0)], False)])


# ---------------------------------------------------------------- the network

def test_the_area_network_has_both_ways_of_two_way_roads_and_the_lights_stop_lines():
    traffic = CityTraffic.start("toa-payoh")
    network = traffic.network
    assert len(traffic.vehicles) == len(network.seeds) == 1010
    two_way = next(r for r in network.roads if not r.oneway)
    back = network.by_key[f"{two_way.road_id}|-1"]
    assert (back.from_node, back.to_node) == (two_way.to_node, two_way.from_node)
    assert network.entries and all(r in network.entries for r in network.large_entries)
    with_lights = [r for r in network.roads if r.stops]
    assert len(with_lights) > 50
    # Every line is on the road at the place its approach names, on the way that runs with the approach
    for junction in route_signals.junctions("toa-payoh")[:20]:
        for approach in junction["approaches"]:
            for direction in (1, -1):
                r = network.by_key.get(f"{approach['road']}|{direction}")
                for at, offset, phase, _ in (r.stops if r else ()):
                    x, y, tx, ty = r.sample(at, 0.0)
                    heading = math.radians(approach["heading_deg"])
                    assert tx * math.cos(heading) + ty * math.sin(heading) >= 0.5
                    assert offset == junction["offset_s"]


def test_the_kerb_lane_is_the_left_one():
    network = RoadNetwork("test", {"models": MODELS, "vehicles": [], "roads": [
        road("two-way", [(0.0, 0.0), (100.0, 0.0)], 1, 2, oneway=False, lanes=2),
        road("one-way", [(0.0, 50.0), (100.0, 50.0)], 3, 4, lanes=2)]}, ())
    two_way, one_way = network.by_key["two-way|1"], network.by_key["one-way|1"]
    assert [two_way.lane_offset(lane) for lane in (0, 1)] == pytest.approx([1.6, 4.8]), "out from the centreline, Singapore's left"
    assert [one_way.lane_offset(lane) for lane in (0, 1)] == pytest.approx([-1.6, 1.6])
    road_found, aside = network.road_under(50.0, 0.0, 1.0, 0.0, 0.7)
    assert road_found is two_way and aside == pytest.approx(4.8)
    road_found, aside = network.road_under(50.0, 0.0, -1.0, 0.0, 0.7)
    assert road_found is network.by_key["two-way|-1"] and aside == pytest.approx(4.8)


# ---------------------------------------------------------------- driving

def test_a_car_queues_behind_a_standing_one_without_touching_it():
    traffic = traffic_on(EAST, [car("leader", "east", 80.0), car("follower", "east", 10.0, speed_kph=100)])
    closed_beyond(traffic, 100.0)
    run(traffic, 40)
    leader, follower = vehicle(traffic, "leader"), vehicle(traffic, "follower")
    assert leader.distance + leader.half < 100.0 and leader.speed < 0.3
    assert follower.speed < 0.3
    gap = (leader.distance - leader.half) - (follower.distance + follower.half)
    assert 0.0 < gap <= city_traffic.MIN_GAP_M + 1.0
    assert traffic.report()["overlaps"] == 0


def test_a_red_light_holds_a_car_and_green_lets_it_go():
    # Phase 1 of a junction at offset 0 is red for the first 35 s of the cycle
    junction = {"id": "j", "offset_s": 0, "approaches": [
        {"road": "east", "heading_deg": 0.0, "stop_x_m": 300.0, "stop_y_m": 0.0, "phase": 1}]}
    traffic = traffic_on(EAST, [car("driver", "east", 100.0, speed_kph=80)], [junction])
    assert traffic.network.by_key["east|1"].stops[0][0] == pytest.approx(300.0)
    clock = run(traffic, 30)
    driver = vehicle(traffic, "driver")
    assert signal_cycle.phase_state(clock, 0, 1)["color"] == "red"
    assert driver.speed < 0.3 and 290.0 < driver.distance + driver.half < 300.0
    run(traffic, 12, clock)
    assert driver.road.road_id == "on" or driver.distance > 300.0, "on through the green"
    assert traffic.red_light_runs == 0


def test_a_hazard_takes_the_cars_it_reaches_off_the_road():
    traffic = traffic_on(EAST, [car("caught", "east", 200.0), car("short", "east", 50.0)])
    closed_beyond(traffic, 190.0)
    traffic.step(DT, DT)
    caught = vehicle(traffic, "caught")
    assert caught.outside or caught.distance < 1.0, "off the map, or back in at the road's start"
    run(traffic, 30, DT)
    short = vehicle(traffic, "short")
    assert short.distance + short.half <= 190.0


# ---------------------------------------------------------------- shuttles

def shuttle_on(traffic, route, along, *, speed=0.0, in_traffic=False):
    record = traffic.shuttle("shuttle-01", SHUTTLE_FRONT_M, 2.3)
    traffic.place_shuttle(record, route, along, speed, DT, in_traffic=in_traffic)
    return record


def test_a_shuttle_stops_for_a_standing_car_in_its_lane():
    traffic = traffic_on(EAST, [car("standing", "east", 50.0)])
    closed_beyond(traffic, 70.0)
    route = ShuttleRoute([(0.0, 0.0), (600.0, 0.0)])
    record = shuttle_on(traffic, route, 20.0, speed=10.0)
    assert record.aside == pytest.approx(0.0), "a one-lane one-way road's only lane is on its centreline"
    plan = traffic.plan_shuttle(record, route, 20.0, 10.0)
    standing = vehicle(traffic, "standing")
    # It may go no further than leaves the minimum gap behind the car, and slows down for it already
    assert plan.cap_m == pytest.approx(50.0 - standing.half - 20.0 - SHUTTLE_FRONT_M - city_traffic.MIN_GAP_M, abs=0.01)
    assert plan.held and plan.limit_mps < 10.0
    assert standing.slot in record.gives_way_to


def test_a_car_queues_behind_a_shuttle_held_in_traffic():
    traffic = traffic_on(EAST, [car("behind", "east", 5.0, speed_kph=100)])
    route = ShuttleRoute([(0.0, 0.0), (600.0, 0.0)])
    record = shuttle_on(traffic, route, 80.0, in_traffic=True)
    for i in range(int(30 / DT)):
        traffic.step(DT, (i + 1) * DT)
        traffic.place_shuttle(record, route, 80.0, 0.0, DT, in_traffic=True)
    behind = vehicle(traffic, "behind")
    assert behind.speed < 0.3
    assert behind.distance + behind.half <= 80.0 - SHUTTLE_FRONT_M
    assert not traffic.shuttle_overlaps()


def test_a_car_never_drives_into_a_shuttle_even_one_giving_way_to_it():
    # The shuttle stands across the car's road; told the shuttle gives way, the car ignores it
    traffic = traffic_on(EAST, [car("ignoring", "east", 10.0, speed_kph=100)])
    route = ShuttleRoute([(150.0, -40.0), (150.0, 40.0)])
    record = shuttle_on(traffic, route, 40.0)
    for i in range(int(20 / DT)):
        record.gives_way_to = {vehicle(traffic, "ignoring").slot}
        traffic.step(DT, (i + 1) * DT)
        assert not traffic.shuttle_overlaps()
    assert vehicle(traffic, "ignoring").distance < 150.0


def test_a_shuttle_body_keeps_inside_a_bend():
    route = ShuttleRoute([(0.0, 0.0), (100.0, 0.0), (100.0, 100.0)])
    x, y, fx, fy = route.body(100.0, 0.0, SHUTTLE_FRONT_M)
    # Front and back on the route either side of the corner, the middle cutting inside it
    assert (x, y) == pytest.approx((96.7, 3.3))
    assert (fx, fy) == pytest.approx((math.sqrt(0.5), math.sqrt(0.5)))


def test_a_body_at_a_route_end_faces_the_way_the_route_ran_whatever_its_last_sliver():
    # A depot's yard path meets its road a few centimetres off its own line: that last sliver must not
    # swing the bus's front across the road into one waiting there
    route = ShuttleRoute([(0.0, 0.0), (0.0, -30.0), (0.03, -30.0)])
    x, y, fx, fy = route.body(route.length, 0.0, SHUTTLE_FRONT_M)
    assert (fx, fy) == pytest.approx((0.0, -1.0), abs=0.01)
    assert (x, y) == pytest.approx((0.0, -30.0), abs=0.05)
    x, y, fx, fy = ShuttleRoute([(0.03, 0.0), (0.0, 0.0), (0.0, -30.0)]).body(0.0, 0.0, SHUTTLE_FRONT_M)
    assert (fx, fy) == pytest.approx((0.0, -1.0), abs=0.01)
    assert (x, y) == pytest.approx((0.0, 0.0), abs=0.05)


def test_the_incident_scene_and_queue_close_the_roads_leading_to_it():
    edges = {"blocked": {"source": 1, "target": 2, "path": [[0.0, 0.0], [100.0, 0.0]]},
             "feeder": {"source": 0, "target": 1, "path": [[-200.0, 0.0], [0.0, 0.0]]},
             "away": {"source": 2, "target": 1, "path": [[100.0, 0.0], [0.0, 0.0]]}}
    plan = incident_hazards.plan(incident_hazards.BLOCK, "blocked", edges, set(), ((0, 0), (100, 0)))
    assert len(plan.lanes) == 1, "the road's own other direction is leaving, not queueing"
    scene, *queue = incident_hazards.hazards(plan, 10.0)
    assert scene == ([(0.0, 0.0), (100.0, 0.0)], True)
    # Ten seconds at 5 m/s: 50 m of the feeder, the way its traffic waits
    assert queue == [([(-50.0, 0.0), (0.0, 0.0)], False)]
    assert incident_hazards.plan(incident_hazards.BLOCK, "blocked", edges, {"feeder"}, ((0, 0), (100, 0))).lanes == []


# ---------------------------------------------------------------- determinism

def test_traffic_survives_a_checkpoint_exactly():
    traffic = CityTraffic.start("toa-payoh")
    run(traffic, 10)
    state = json.dumps(traffic.state())
    restored = CityTraffic.from_state(json.loads(state))
    assert json.dumps(restored.state()) == state
    run(traffic, 10, 10)
    run(restored, 10, 10)
    assert restored.state() == traffic.state()


def test_a_staged_copy_drives_on_without_touching_the_original():
    traffic = CityTraffic.start("toa-payoh")
    run(traffic, 5)
    before = traffic.state()
    copy = deepcopy(traffic)
    run(copy, 5, 5)
    assert traffic.state() == before
    run(traffic, 5, 5)
    assert traffic.state() == copy.state()


# ---------------------------------------------------------------- the live session

def stop_line_crossed_on_red(actor, before, junctions, known):
    """A shuttle whose front crossed one of its route's stop lines this tick while the light was red."""
    for shuttle in actor.shuttles:
        route_id, progress = before[shuttle.ID]
        if shuttle.route_ID != route_id or shuttle.progress < progress:
            continue
        waypoints = actor._routes[route_id]["waypoints"]
        if known.get(route_id, (None,))[0] is not waypoints:
            known[route_id] = (waypoints, route_signals.stop_lines([(p.x, p.y) for p in waypoints], junctions))
        for line in known[route_id][1]:
            colour = signal_cycle.phase_state(actor.timestamp_second, line.offset_s, line.phase)["color"]
            if progress + SHUTTLE_FRONT_M <= line.progress_m < shuttle.progress + SHUTTLE_FRONT_M and colour == "red":
                return shuttle.ID
    return None


@pytest.mark.traffic
def test_shuttles_queue_behind_the_traffic_and_never_run_a_red_or_touch_a_vehicle(monkeypatch):
    # The queue at a light, without the walkers: with them this session's shuttles are held as often at
    # the crossings before it (test_city_pedestrians), and the queue this looks for does not form in its
    # five minutes
    monkeypatch.setenv(walk_network.PEDESTRIANS_VARIABLE, "0")
    actor = IntegratedSessionActor(0, session_id="traffic-test")
    actor.is_paused = False
    actor.modes = {s.ID: "IN_SERVICE" for s in actor.shuttles}
    # Only the lights and the traffic: an incident's closure or wreck stops a shuttle as long as it lasts
    # (test_traffic_events)
    actor.incident_due_tick = 10**12
    junctions = route_signals.junctions("toa-payoh")
    known, stalled, queued = {}, {s.ID: 0 for s in actor.shuttles}, 0
    distance = {s.ID: 0.0 for s in actor.shuttles}
    for _ in range(6000):  # Five minutes
        before = {s.ID: (s.route_ID, s.progress) for s in actor.shuttles}
        actor.tick()
        assert stop_line_crossed_on_red(actor, before, junctions, known) is None
        assert actor.traffic.shuttle_overlaps() == []
        for shuttle in actor.shuttles:
            moved = shuttle.progress - before[shuttle.ID][1]
            distance[shuttle.ID] += max(0.0, moved)
            stalled[shuttle.ID] = stalled[shuttle.ID] + 1 if actor.modes[shuttle.ID] == "IN_SERVICE" and moved == 0 else 0
            # No light holds up a shuttle for longer than a red lasts, nor the traffic for a whole minute
            assert stalled[shuttle.ID] * actor.STEP_S < 60, f"shuttle {shuttle.ID} stuck"
            record = actor._traffic_shuttle(shuttle)
            waiting_for = [actor.traffic.vehicles[slot] for slot in record.gives_way_to]
            # Standing behind a car that is itself waiting at a red light: the queue the 3D view draws
            if moved == 0 and record.in_traffic and any(
                    any(signal_cycle.phase_state(actor.timestamp_second, o, p)["color"] != "green"
                        and 0 <= at - (v.distance + v.half) < 30 for at, o, p, _ in v.road.stops)
                    for v in waiting_for):
                queued += 1
    report = actor.traffic.report()
    assert report["red_light_runs"] == 0 and report["overlaps"] == 0
    assert queued > 0, "a shuttle queued behind traffic waiting at a light"
    assert min(distance.values()) > 1000, "every shuttle kept going"


@pytest.mark.traffic
def test_a_shuttle_brakes_evenly_and_pulls_away_no_faster_than_the_bus_can():
    actor = IntegratedSessionActor(0, session_id="traffic-test")
    actor.is_paused = False
    actor.modes = {s.ID: "IN_SERVICE" for s in actor.shuttles}
    speeds = {s.ID: 0.0 for s in actor.shuttles}
    for _ in range(1200):
        actor.tick()
        for shuttle in actor.shuttles:
            speed = actor._traffic_shuttle(shuttle).speed
            assert speed <= 10.0 + 1e-6
            assert speed - speeds[shuttle.ID] <= city_traffic.SHUTTLE_ACCEL * actor.STEP_S + 1e-6
            speeds[shuttle.ID] = speed


def vehicles_of(frame):
    """A frame's vehicles, unpacked: (seed index, x cm, y cm, heading in tenths of a degree, speed cm/s)."""
    vehicles = unpack_frame(base64.b64decode(frame["vehicles"]))
    assert len(vehicles) == frame["vehicle_count"]
    return vehicles


@pytest.mark.traffic
def test_frames_carry_the_traffic_and_the_shuttles_bodies():
    actor = IntegratedSessionActor(0, session_id="traffic-test")
    frame = actor.traffic_frame()
    vehicles = vehicles_of(frame)
    assert frame["tick"] == 0 and 0 < len(vehicles) <= len(actor.traffic.network.seeds)
    # Each record is where the traffic has the vehicle, to the centimetre and tenth of a degree
    for seed, x, y, heading, speed in vehicles[:50]:
        vehicle = next(v for v in actor.traffic.vehicles if v.seed == seed)
        assert not vehicle.outside
        assert abs(x / 100 - vehicle.x) <= 0.005 and abs(y / 100 - vehicle.y) <= 0.005
        assert abs(heading / 10 - math.degrees(math.atan2(vehicle.fy, vehicle.fx))) <= 0.05
        assert abs(speed / 100 - vehicle.speed) <= 0.005
    assert [s["vehicle_id"] for s in frame["shuttles"]] == [v.vehicle_id for v in actor.domain.vehicles]
    # A shuttle's body is in its lane, beside its route's line
    first = frame["shuttles"][0]
    position = actor.shuttles[0].position
    assert math.hypot(first["x_m"] - position.x, first["y_m"] - position.y) < 8.0
    assert IntegratedSessionActor.TRAFFIC is True


def test_without_traffic_a_session_drives_as_before():
    actor = IntegratedSessionActor(0, session_id="no-traffic")
    assert actor.traffic is None and actor.traffic_frame() is None
    assert checkpoint(actor)["workflow"]["traffic"] is None


async def _manual_clock(self, session):
    await asyncio.Event().wait()


@pytest.mark.traffic
def test_a_recovered_session_drives_its_traffic_on_exactly(tmp_path, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", _manual_clock)

    async def exercise(path, *, recover_at=None, ticks=400):
        actor = IntegratedSessionActor(1000, session_id="traffic-recovery")
        with TrainingStore(path) as store:
            store.create_session(actor.generate_snapshot(), runtime_state=checkpoint(actor))
            service = TrainingSessionService(store)
            await service.initialize()
            # A pre-trip route approval for shuttle-01, then the start
            draft = actor.definition.routes.preview(store.snapshot(actor.session_id), RouteEditRequest(
                route_id="route-peer-a", expected_route_revision=1, draft_revision=1))
            approved = await service.approve_route("operator", actor.session_id, RouteApprovalRequest(
                preview_id=draft.preview_id, preview_revision=draft.preview_revision, command_id="route",
                idempotency_key="route", target_vehicle_ids=["shuttle-01"], reason="Pre-trip route"))
            assert approved.status == "APPLIED"
            snapshot = store.snapshot(actor.session_id)
            start = await service.submit("operator", DispatcherCommand(contract_version="2.0", command_id="start",
                idempotency_key="start", session_id=actor.session_id, command_type="START", expected_epoch=1,
                expected_revision=snapshot.session.aggregate_revision, target_type="SESSION", target_id=actor.session_id,
                payload={}))
            assert start.status == "APPLIED"
            for tick in range(ticks):
                if tick == recover_at:
                    await service.close()
                    service = TrainingSessionService(store)
                    await service.initialize()
                await service.step(service.sessions[actor.session_id])
            live = service.sessions[actor.session_id].actor
            frames = service.traffic(actor.session_id, live.current_tick - 6)
            result = (live.current_tick, live.traffic.state(), [s.model_dump(mode="json") for s in live.shuttles])
            await service.close()
            return result, frames

    (tick, traffic, shuttles), frames = asyncio.run(exercise(tmp_path / "straight.sqlite"))
    (tick_r, traffic_r, shuttles_r), _ = asyncio.run(exercise(tmp_path / "recovered.sqlite", recover_at=200))
    assert tick == tick_r == 400
    assert traffic_r == traffic and shuttles_r == shuttles
    # The latest frames, one a published tick, as the 3D view polls them
    assert [json.loads(frame)["tick"] for frame in frames] == [396, 398, 400]


async def _start(service, store, actor):
    """Approves a pre-trip route for shuttle-01 and starts the session."""
    draft = actor.definition.routes.preview(store.snapshot(actor.session_id), RouteEditRequest(
        route_id="route-peer-a", expected_route_revision=1, draft_revision=1))
    approved = await service.approve_route("operator", actor.session_id, RouteApprovalRequest(
        preview_id=draft.preview_id, preview_revision=draft.preview_revision, command_id="route",
        idempotency_key="route", target_vehicle_ids=["shuttle-01"], reason="Pre-trip route"))
    assert approved.status == "APPLIED"
    snapshot = store.snapshot(actor.session_id)
    start = await service.submit("operator", DispatcherCommand(contract_version="2.0", command_id="start",
        idempotency_key="start", session_id=actor.session_id, command_type="START", expected_epoch=1,
        expected_revision=snapshot.session.aggregate_revision, target_type="SESSION", target_id=actor.session_id,
        payload={}))
    assert start.status == "APPLIED"


@pytest.mark.traffic
def test_the_traffic_is_checkpointed_apart_about_once_a_second(tmp_path, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", _manual_clock)

    async def exercise():
        actor = IntegratedSessionActor(1000, session_id="traffic-apart")
        with TrainingStore(tmp_path / "apart.sqlite") as store:
            store.create_session(actor.generate_snapshot(), runtime_state=checkpoint(actor))
            service = TrainingSessionService(store)
            await service.initialize()
            await _start(service, store, actor)
            saved = []
            for _ in range(70):
                await service.step(service.sessions[actor.session_id])
                with store._reading() as connection:
                    runtime, = connection.execute("SELECT checkpoint_json FROM session_runtimes").fetchone()
                    traffic_tick, = connection.execute("SELECT tick FROM session_traffic").fetchone()
                runtime = json.loads(runtime)
                saved.append((runtime["tick"], traffic_tick, "traffic" in runtime, runtime["workflow"]["traffic"]))
            await service.close()
            return saved

    saved = asyncio.run(exercise())
    # Every publication checkpoints the rest of the state, which only notes that the traffic is apart
    assert all(not inline and workflow == integrated_actor.TRAFFIC_APART for _, _, inline, workflow in saved)
    # The traffic only every TRAFFIC_CHECKPOINT_TICKS, so never further behind than that
    every = integrated_actor.TRAFFIC_CHECKPOINT_TICKS
    assert all(0 <= tick - traffic_tick < every for tick, traffic_tick, _, _ in saved)
    assert len({traffic_tick for _, traffic_tick, _, _ in saved}) <= 70 // every + 1


@pytest.mark.traffic
def test_a_session_recovered_between_traffic_checkpoints_drives_its_traffic_on(tmp_path, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", _manual_clock)

    async def exercise():
        actor = IntegratedSessionActor(1000, session_id="traffic-between")
        with TrainingStore(tmp_path / "between.sqlite") as store:
            store.create_session(actor.generate_snapshot(), runtime_state=checkpoint(actor))
            service = TrainingSessionService(store)
            await service.initialize()
            await _start(service, store, actor)
            for _ in range(212):
                await service.step(service.sessions[actor.session_id])
            with store._reading() as connection:
                traffic_tick, = connection.execute("SELECT tick FROM session_traffic").fetchone()
            await service.close()
            service = TrainingSessionService(store)
            await service.initialize()
            restored = service.sessions[actor.session_id].actor
            result = {"tick": restored.current_tick, "traffic_tick": traffic_tick,
                      "time_s": restored.traffic.time_s, "overlaps": restored.traffic.shuttle_overlaps(),
                      "saved_tick": restored.traffic_saved_tick}
            # The store stayed open, so the session was not paused by a store recovery
            assert not restored.is_paused
            before = [s.progress for s in service.sessions[actor.session_id].actor.shuttles]
            overlaps = []
            for _ in range(200):
                await service.step(service.sessions[actor.session_id])
                overlaps += service.sessions[actor.session_id].actor.traffic.shuttle_overlaps()
            live = service.sessions[actor.session_id].actor
            result.update(after=[s.progress for s in live.shuttles], before=before, overlaps_after=overlaps,
                          frames=service.traffic(actor.session_id, live.current_tick - 4))
            await service.close()
            return result

    result = asyncio.run(exercise())
    # Recovered between traffic checkpoints: the saved traffic is older than the session...
    assert result["traffic_tick"] < result["tick"] and result["saved_tick"] == result["traffic_tick"]
    # ...and is driven on to the session's own time, with nothing left under a shuttle
    assert result["time_s"] == pytest.approx(result["tick"] * IntegratedSessionActor.STEP_S)
    assert result["overlaps"] == []
    # It drives on: shuttles move, never into a vehicle, and frames keep coming
    assert result["after"] != result["before"]
    assert result["overlaps_after"] == []
    assert result["frames"]


@pytest.mark.traffic
@pytest.mark.parametrize("damage", ["hash", "missing", "ahead"])
def test_a_damaged_traffic_checkpoint_fails_its_session_rather_than_being_trusted(tmp_path, monkeypatch, damage):
    import hashlib
    import sqlite3
    monkeypatch.setattr(TrainingSessionService, "_run", _manual_clock)
    database = tmp_path / "damaged.sqlite"
    actor = IntegratedSessionActor(1000, session_id="traffic-damaged")
    with TrainingStore(database) as store:
        store.create_session(actor.generate_snapshot(), runtime_state=checkpoint(actor))
    with sqlite3.connect(database) as connection:
        if damage == "hash":
            connection.execute("UPDATE session_traffic SET state_hash='bad'")
        elif damage == "missing":
            connection.execute("DELETE FROM session_traffic")
        else:
            raw, = connection.execute("SELECT traffic_json FROM session_traffic").fetchone()
            saved = json.loads(raw)
            saved["tick"] += 2
            raw = json.dumps(saved)
            connection.execute("UPDATE session_traffic SET traffic_json=?, state_hash=?",
                               (raw, hashlib.sha256(raw.encode()).hexdigest()))

    async def recover():
        with TrainingStore(database) as store:
            service = TrainingSessionService(store)
            await service.initialize()
            try:
                events = store.replay(actor.session_id, 0, 100).events
                return len(service.sessions), store.snapshot(actor.session_id).session.status, events[-1].data
            finally:
                await service.close()

    assert asyncio.run(recover()) == (0, "FAILED", {"status": "FAILED", "reason": "CORRUPT_CHECKPOINT"})


def test_sessions_with_traffic_are_admitted_only_up_to_their_limit(tmp_path, monkeypatch):
    from contracts.v2.sessions import CreateSessionRequest
    from src.persistence.training_store import TrainingStoreError
    monkeypatch.setattr(TrainingSessionService, "_run", _manual_clock)
    monkeypatch.setattr(TrainingSessionService, "MAX_TRAFFIC_SESSIONS", 1)
    monkeypatch.setattr(IntegratedSessionActor, "TRAFFIC", True)

    async def exercise():
        with TrainingStore(tmp_path / "limit.sqlite") as store:
            service = TrainingSessionService(store)
            request = CreateSessionRequest(scenario_revision_id=training_sessions.SCENARIO_SELECTOR)
            await service.create(request, "operator", "first")
            try:
                with pytest.raises(TrainingStoreError) as refused:
                    await service.create(request, "operator", "second")
                # A session without traffic still has room
                await service.create(CreateSessionRequest(scenario_revision_id=training_sessions.SCENARIO_REVISION),
                                     "operator", "plain")
                return refused.value.code, service.traffic_sessions(), len(service.sessions)
            finally:
                await service.close()

    assert asyncio.run(exercise()) == ("QUEUE_FULL", 1, 2)


def test_a_session_without_traffic_has_no_frames(tmp_path, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", _manual_clock)

    async def exercise():
        actor = IntegratedSessionActor(1000, session_id="no-traffic-frames")
        with TrainingStore(tmp_path / "plain.sqlite") as store:
            store.create_session(actor.generate_snapshot(), runtime_state=checkpoint(actor))
            service = TrainingSessionService(store)
            await service.initialize()
            try:
                with pytest.raises(Exception) as refused:
                    service.traffic(actor.session_id, -1)
                return refused.value
            finally:
                await service.close()

    assert getattr(asyncio.run(exercise()), "code", None) == "INVALID_REFERENCE"


def test_bodies_that_only_touch_do_not_overlap():
    assert not overlap(0, 0, 1, 0, 2, 1, 4.05, 0, 1, 0, 2, 1)
    assert overlap(0, 0, 1, 0, 2, 1, 3.5, 0, 1, 0, 2, 1)


@pytest.mark.traffic
def test_the_traffic_endpoint_serves_the_frames(tmp_path, monkeypatch):
    from fastapi.testclient import TestClient
    from src.api.routes.training_v2 import get_actor_id, get_training_store
    from src.app import app
    from src.services.integrated_actor import SCENARIO_SELECTOR
    monkeypatch.setattr(TrainingSessionService, "_run", _manual_clock)
    previous = dict(app.dependency_overrides)
    try:
        with TrainingStore(tmp_path / "endpoint.sqlite") as store:
            app.dependency_overrides[get_training_store] = lambda: store
            app.dependency_overrides[get_actor_id] = lambda: "traffic-operator"
            with TestClient(app) as client:
                created = client.post("/api/v2/sessions", headers={"Idempotency-Key": "traffic-attempt"},
                                      json={"scenario_revision_id": SCENARIO_SELECTOR, "seed": 1000})
                assert created.status_code == 201
                session_id = created.json()["session_id"]
                reply = client.get(f"/api/v2/sessions/{session_id}/traffic")
                assert reply.status_code == 200
                body = reply.json()
                # Not yet started: the traffic where it stands, so the 3D view can draw it
                assert body["session_id"] == session_id and [f["tick"] for f in body["frames"]] == [0]
                assert len(body["frames"][0]["shuttles"]) == 5 and len(vehicles_of(body["frames"][0])) > 800
                assert client.get(f"/api/v2/sessions/{session_id}/traffic?after_tick=0").json()["frames"] == []
                # Asked to wait, a reply with no frame yet waits that long for one, then says none came
                started = time.monotonic()
                waited = client.get(f"/api/v2/sessions/{session_id}/traffic?after_tick=0&wait_ms=150")
                assert waited.status_code == 200 and waited.json()["frames"] == []
                assert time.monotonic() - started >= 0.1
                assert client.get(f"/api/v2/sessions/{session_id}/traffic?wait_ms=6000").status_code == 422
                assert client.get("/api/v2/sessions/no-such-session/traffic").status_code == 404
    finally:
        app.dependency_overrides.clear()
        app.dependency_overrides.update(previous)


def test_packed_vehicles_read_back_exactly():
    records = [(0, 0, 0, 0, 0), (1009, -123456, 98765, -1800, 1550), (65535, 2**31 - 1, -(2**31 - 1), 1800, 65535)]
    packed = b"".join(FRAME_RECORD.pack(*record) for record in records)
    assert len(packed) == 14 * len(records)
    assert unpack_frame(packed) == records
    with pytest.raises(ValueError):
        unpack_frame(packed[:-1])


def test_a_late_tick_is_caught_up_on_and_only_a_long_stall_is_let_go():
    step = 50_000_000
    # On time: one step on
    assert next_deadline(1_000_000_000, 1_000_000_000, step) == 1_050_000_000
    # A tick that ran 30 ms over does not push back the next: it comes at once, then the pace resumes
    assert next_deadline(1_000_000_000, 1_080_000_000, step) == 1_050_000_000
    # A stall longer than the backlog keeps only the backlog to catch up on
    backlog = round(training_sessions.MAX_TICK_BACKLOG_S * 1e9)
    assert next_deadline(1_000_000_000, 5_000_000_000, step) == 5_000_000_000 - backlog
    # A session whose ticks alternate 80 ms and 10 ms keeps the wall clock's pace
    deadline = now = 0
    for tick in range(200):
        now = max(now, deadline) + (80_000_000 if tick % 2 == 0 else 10_000_000)
        deadline = next_deadline(deadline, now, step)
    assert abs(deadline - 200 * step) <= step


async def _started(store, actor):
    service = TrainingSessionService(store)
    await service.initialize()
    draft = actor.definition.routes.preview(store.snapshot(actor.session_id), RouteEditRequest(
        route_id="route-peer-a", expected_route_revision=1, draft_revision=1))
    approved = await service.approve_route("operator", actor.session_id, RouteApprovalRequest(
        preview_id=draft.preview_id, preview_revision=draft.preview_revision, command_id="route",
        idempotency_key="route", target_vehicle_ids=["shuttle-01"], reason="Pre-trip route"))
    assert approved.status == "APPLIED"
    snapshot = store.snapshot(actor.session_id)
    start = await service.submit("operator", DispatcherCommand(contract_version="2.0", command_id="start",
        idempotency_key="start", session_id=actor.session_id, command_type="START", expected_epoch=1,
        expected_revision=snapshot.session.aggregate_revision, target_type="SESSION", target_id=actor.session_id,
        payload={}))
    assert start.status == "APPLIED"
    return service


@pytest.mark.traffic
def test_a_frame_comes_with_every_traffic_step_whichever_ticks_publish(tmp_path, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", _manual_clock)

    async def exercise():
        actor = IntegratedSessionActor(1000, session_id="traffic-steps")
        with TrainingStore(tmp_path / "steps.sqlite") as store:
            store.create_session(actor.generate_snapshot(), runtime_state=checkpoint(actor))
            service = await _started(store, actor)
            try:
                session = service.sessions[actor.session_id]
                # Snapshots now publish on the ticks the traffic does not step
                session.actor.command_tick += 1
                first = session.actor.current_tick
                for _ in range(20):
                    await service.step(session)
                frames = [json.loads(frame) for frame in service.traffic(actor.session_id, first)]
                return first, session.actor, frames
            finally:
                await service.close()

    first, live, frames = asyncio.run(exercise())
    assert live.current_tick == first + 20
    assert [f["tick"] for f in frames] == [t for t in range(first + 1, first + 21) if t % 2 == 0][-10:]
    # Each frame is stamped with the moment it shows, and the traffic stood there then
    assert all(f["simulation_time_s"] == round(f["tick"] * live.STEP_S, 3) for f in frames)
    assert frames[-1]["vehicles"] == live.traffic_frame()["vehicles"]


@pytest.mark.traffic
def test_a_view_waiting_for_traffic_hears_of_the_next_frame_at_once(tmp_path, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", _manual_clock)

    async def exercise():
        actor = IntegratedSessionActor(1000, session_id="traffic-waits")
        with TrainingStore(tmp_path / "waits.sqlite") as store:
            store.create_session(actor.generate_snapshot(), runtime_state=checkpoint(actor))
            service = await _started(store, actor)
            try:
                session = service.sessions[actor.session_id]
                while not session.actor.traffic_frame_due() or not session.frames:
                    await service.step(session)
                newest = session.actor.current_tick
                waiting = asyncio.create_task(service.wait_for_traffic(actor.session_id, newest, 30))
                await asyncio.sleep(0.05)
                assert not waiting.done(), "nothing new yet"
                started = time.monotonic()
                await service.step(session)
                await service.step(session)
                frames = await asyncio.wait_for(waiting, 5)
                return newest, frames, time.monotonic() - started
            finally:
                await service.close()

    newest, frames, waited = asyncio.run(exercise())
    assert [json.loads(frame)["tick"] for frame in frames] == [newest + 2]
    assert waited < 5
