"""
Tests for the pedestrians the backend's traffic walks (phases 3 and 4 of sidewalks_plan.md): the walk
network (backend/src/traffic/walk_network.py) and the walkers of both traffic engines (city_traffic.py,
native_traffic.py).

Description:
    On small made-up roads and walkways: a walker steps off a signalled crossing only on the red of the
    traffic it crosses, with time to get over; at a zebra a car that can stop gives way to a walker waiting,
    one that cannot drives on and the walker waits for it; a steady stream of walkers holds a car only so
    long; a car turning into a road stops for a walker on a crossing there; and a shuttle stops short of
    an occupied crossing on its route. On the real Toa Payoh network: no vehicle or shuttle ever touches a
    walker, through signal faults and incidents too; the walkers survive a checkpoint and a staged copy
    exactly; and the 3D view gets them in the traffic frames, with a shuttle the walkers hold up saying so.
    Each runs on both engines (the native one where it is built), but for those that retune the walkers'
    constants in Python, which the native engine has compiled in. test_native_traffic holds the two
    engines' walkers to each other bit for bit.
"""

import base64
import json
import math
from copy import deepcopy

import pytest

from contracts.v2.stream import StreamTrafficFrame
from src.services.integrated_actor import IntegratedSessionActor
from src.traffic import city_traffic, native_traffic, signal_cycle, walk_network
from src.traffic.city_traffic import (ON_CROSSING, PED_RECORD, WAITING_AT_KERB, ZEBRA_COURTESY_S, ZEBRA_RELEASE_S,
                                      CityTraffic, ShuttleRoute, light)
from src.traffic.native_traffic import NativeCityTraffic
from src.traffic.road_network import RoadNetwork
from src.traffic.walk_network import WalkNetwork

MODELS = {"car": {"length_m": 4.5, "width_m": 1.8, "height_m": 1.5}}
DT = 0.1
# A vehicle this slow short of a crossing has given way: with per-lane release it need not stand, only wait
# until the walker is past its lane
GAVE_WAY_MPS = 1.0


@pytest.fixture(params=["python", "native"])
def engine(request):
    """The traffic class of each engine; the native one where it is built."""
    if request.param == "native":
        usable, reason = native_traffic.status()
        if not usable:
            pytest.skip(f"native traffic engine unavailable: {reason}")
        return NativeCityTraffic
    return CityTraffic


def road(identifier, points, start, end, *, oneway=True, lanes=1, speed_kph=50):
    return {"id": identifier, "class": "primary", "from_node": start, "to_node": end, "oneway": oneway,
            "lanes": lanes, "speed_kph": speed_kph, "points_m": points}


def car(identifier, road_id, distance, *, speed_kph=0.0):
    # A seed's speed is taken at half, as the sample starts its vehicles
    return {"id": identifier, "model": "car", "road": road_id, "direction": 1, "lane": 0,
            "distance_m": distance, "speed_kph": speed_kph * 2, "color": "#ffffff"}


# A straight 600 m one-way road east, one lane (its asphalt 2.2 m either side of its line), and on
EAST = [road("east", [(0.0, 0.0), (600.0, 0.0)], 1, 2), road("on", [(600.0, 0.0), (900.0, 0.0)], 2, 3)]


def crossing_at(x, *, control="zebra", junction=None, phase=0, y_road=2.2, y_node=2.6, first_node=0):
    """A crossing over the east road at x, from a node on the north footpath to one on the south, with short
    dead-end footpaths west of each, so a walker that has crossed turns about and crosses again."""
    a, b, c, d = first_node, first_node + 1, first_node + 2, first_node + 3
    nodes = [(x, y_node), (x, -y_node), (x - 3.0, y_node), (x - 3.0, -y_node)]
    crossing = {"kind": "crossing", "from": a, "to": b, "points_m": [[x, y_node], [x, y_road], [x, -y_road], [x, -y_node]],
                "width_m": 3.0, "lanes": 4, "crossing_id": f"crossing-{x}", "control": control}
    if junction is not None:
        crossing.update(junction_id=junction, phase=phase)
    ways = [crossing,
            {"kind": "sidewalk", "from": c, "to": a, "points_m": [[x - 3.0, y_node], [x, y_node]], "width_m": 1.8, "lanes": 2},
            {"kind": "sidewalk", "from": d, "to": b, "points_m": [[x - 3.0, -y_node], [x, -y_node]], "width_m": 1.8, "lanes": 2}]
    return nodes, ways


def walks_on(network, nodes, ways, seeds, junctions=()):
    data = {"area": "test", "source_hash": "test-walks",
            "nodes": [{"id": i, "xy_m": list(p), "kind": "joint"} for i, p in enumerate(nodes)],
            "walkways": [dict(way, id=i) for i, way in enumerate(ways)],
            "seeds": [{"id": f"ped-{i:04d}", "walkway": walkway, "distance_m": distance, "speed_mps": speed,
                       "height_m": 1.7, "model": 0} for i, (walkway, distance, speed) in enumerate(seeds)]}
    return WalkNetwork("test", data, network, junctions)


def traffic_with(roads, cars, nodes, ways, seeds, junctions=(), engine=CityTraffic):
    network = RoadNetwork("test", {"models": MODELS, "vehicles": cars, "roads": roads}, tuple(junctions))
    traffic = engine.on(network, walks_on(network, nodes, ways, seeds, junctions))
    assert type(traffic) is engine
    return traffic


def vehicle(traffic, identifier):
    return next(v for v in traffic.vehicles if v.id == identifier)


def on_road_part(walker):
    way = walker.way
    return way.crossing is not None and way.kerb_in <= walker.distance <= way.kerb_out


def clear_of(vehicle):
    """How far a walker must be either side of the middle of a vehicle's lane, along a crossing square to its
    road, for the vehicle to be let through it (per-lane release): its half width, the margin and the walker's
    half (the crossings here are square to their roads, so the band adds no slant)."""
    return vehicle.half_width + city_traffic.LANE_RELEASE_MARGIN_M + walk_network.WALKER_HALF_M - 1e-9


def band_of(traffic, road_key):
    """Where the only crossing over a road starts and ends along it."""
    at, _, band, _ = traffic.walks.by_road[traffic.network.by_key[road_key].index][0]
    return at - band, at + band


@pytest.fixture
def always_crossing(monkeypatch):
    """Walkers take every crossing they come to and never stand about, for a steady stream over one."""
    monkeypatch.setattr(city_traffic, "CROSSINGS_DUE_MIN", 0)
    monkeypatch.setattr(city_traffic, "CROSSINGS_DUE_MAX", 0)
    monkeypatch.setattr(city_traffic, "TAKE_CROSSING_CHANCE", 1.0)
    monkeypatch.setattr(city_traffic, "PAUSE_CHANCE", 0.0)


# ---------------------------------------------------------------- the walk network

def test_the_walk_network_has_both_ways_of_each_walkway_and_the_roads_each_crossing_cuts():
    walks = walk_network.walk_network("toa-payoh")
    assert walks is not None and len(walks.seeds) == 600
    for way in walks.ways:
        back = walks.ways[way.back]
        assert back.back == way.index and (back.from_node, back.to_node) == (way.to_node, way.from_node)
        assert back.length == pytest.approx(way.length)
        if way.crossing is not None:
            assert 0 <= way.kerb_in < way.kerb_out <= way.length + 1e-9
            assert way.kerb_in == pytest.approx(back.length - back.kerb_out)
    # Nearly every crossing cuts a road the traffic drives; each lies on the road where the file says
    cutting = [c for c in walks.crossings if c.roads]
    assert len(cutting) >= 0.95 * len(walks.crossings)
    for crossing in cutting[:60]:
        for road_index, at, band, slant in crossing.roads:
            x, y, _, _ = walks.network.roads[road_index].sample(at, 0.0)
            # On the crossing's line, between its kerbs
            ax, ay = x - crossing.cx, y - crossing.cy
            assert abs(ax * crossing.dy - ay * crossing.dx) < 0.05
            assert abs(ax * crossing.dx + ay * crossing.dy) <= crossing.half_length + 0.6
            assert band >= crossing.half_width
            assert (at, crossing, band, slant) in walks.by_road[road_index]


def test_a_signalled_crossing_without_its_light_is_left_out():
    network = RoadNetwork("test", {"models": MODELS, "vehicles": [], "roads": EAST}, ())
    nodes, ways = crossing_at(300.0, control="signal", junction="J1")
    without = walks_on(network, nodes, ways, [])
    assert without.crossings == [] and without.without_light == 1
    with_light = walks_on(network, nodes, ways, [], junctions=[{"id": "J1", "offset_s": 7, "approaches": []}])
    assert len(with_light.crossings) == 1 and with_light.crossings[0].offset == 7


def test_pedestrians_walk_unless_switched_off(monkeypatch, engine):
    monkeypatch.delenv(walk_network.PEDESTRIANS_VARIABLE, raising=False)
    assert len(engine.start("toa-payoh").walkers) == 600
    monkeypatch.setenv(walk_network.PEDESTRIANS_VARIABLE, "0")
    traffic = engine.start("toa-payoh")
    assert traffic.walkers == [] and "walkers" not in traffic.state()
    assert traffic.pedestrian_frame() == (b"", 0)
    monkeypatch.setenv(walk_network.PEDESTRIANS_VARIABLE, "sometimes")
    with pytest.raises(ValueError):
        engine.start("toa-payoh")


def test_the_native_engine_walks_pedestrians_too(monkeypatch):
    monkeypatch.setenv(walk_network.PEDESTRIANS_VARIABLE, "1")
    monkeypatch.setenv(native_traffic.ENGINE_VARIABLE, "auto")
    expected = NativeCityTraffic if native_traffic.status()[0] else CityTraffic
    assert native_traffic.engine() is expected


# ---------------------------------------------------------------- crossing

def test_a_walker_steps_off_a_signalled_crossing_only_on_red_and_is_over_before_green(always_crossing):
    junction = {"id": "J1", "offset_s": 0, "approaches": []}
    nodes, ways = crossing_at(300.0, control="signal", junction="J1")
    seeds = [(0, 0.0, 1.0), (0, 0.0, 1.4), (1, 1.0, 1.2), (2, 2.0, 1.1), (1, 0.5, 1.5)]
    traffic = traffic_with(EAST, [], nodes, ways, seeds, [junction])
    granted = {w.slot: w.granted for w in traffic.walkers}
    started = 0
    for i in range(round(2 * signal_cycle.CYCLE_S / DT)):
        time_s = (i + 1) * DT
        traffic.step(DT, time_s)
        colour = light(time_s, 0, 0)
        for walker in traffic.walkers:
            if walker.granted != -1 and granted[walker.slot] != walker.granted:
                started += 1
                assert colour == 2, f"stepped off on {colour} at {time_s:.1f}s"
            granted[walker.slot] = walker.granted
            if colour == 0:
                assert not on_road_part(walker), f"{walker.id} still on the road at green, {time_s:.1f}s"
    assert started >= 5


def test_at_a_zebra_a_car_that_can_stop_gives_way_and_one_that_cannot_drives_on(engine):
    nodes, ways = crossing_at(300.0)
    # The near car's front is 8 m short of the crossing at 40 km/h, too close to stop; the far one 60 m.
    # A slow walker, so the far one comes before it is over
    traffic = traffic_with(EAST, [car("near", "east", 288.0, speed_kph=40), car("far", "east", 238.0, speed_kph=40)],
                           nodes, ways, [(0, 0.0, 0.6)], engine=engine)
    start, end = band_of(traffic, "east|1")
    near, far, walker = vehicle(traffic, "near"), vehicle(traffic, "far"), traffic.walkers[0]
    near_slowest, far_stopped, crossed, waited = math.inf, False, False, False
    for i in range(round(40 / DT)):
        traffic.step(DT, (i + 1) * DT)
        assert traffic.pedestrian_hits() == []
        waited = waited or walker.state == WAITING_AT_KERB
        # The road runs east from x = 0, and on: along it is x, whichever of the two a car is on
        if near.x - near.half < end + 1:
            near_slowest = min(near_slowest, near.speed)
        if walker.state == ON_CROSSING and not crossed:
            # Not let on until the near car is past
            assert near.x - near.half > end
            crossed = True
        if on_road_part(walker) and abs(walker.y) < clear_of(far):
            # Never in the crossing while the walker is on the road in its lane (it crosses again later, back
            # from its footpath); once the walker is past its lane, it may go (per-lane release)
            assert not (far.x + far.half > start and far.x - far.half < end)
        # It gives way: all but stops, and goes on once the walker is past its lane
        if far.x + far.half < start and far.speed < GAVE_WAY_MPS:
            far_stopped = True
    assert waited and crossed and far_stopped
    assert near_slowest > 10.0, "the near car drove on at its speed"
    assert far.x - far.half > end, "and the far one went on after the walker"


def test_a_steady_stream_over_a_zebra_holds_a_car_only_so_long(always_crossing):
    nodes, ways = crossing_at(300.0)
    seeds = [(i % 3, 0.4 * (i // 3), 1.0 + 0.05 * (i % 7)) for i in range(16)]
    traffic = traffic_with(EAST, [car("car", "east", 200.0, speed_kph=40)], nodes, ways, seeds)
    start, end = band_of(traffic, "east|1")
    held = longest = 0.0
    clear_s = 4.4 / 1.0
    for i in range(round(90 / DT)):
        traffic.step(DT, (i + 1) * DT)
        assert traffic.pedestrian_hits() == []
        moving = vehicle(traffic, "car")
        held = held + DT if moving.speed < 0.3 and moving.distance + moving.half < start else 0.0
        longest = max(longest, held)
        if moving.distance - moving.half > end:
            break
    else:
        pytest.fail("the car never got over the crossing")
    assert 0 < longest <= ZEBRA_COURTESY_S + ZEBRA_RELEASE_S + clear_s + 1.0


def test_a_car_turning_into_a_road_stops_for_a_walker_on_a_crossing_there(engine):
    roads = [road("north", [(310.0, -100.0), (310.0, 0.0)], 9, 1), road("east", [(310.0, 0.0), (600.0, 0.0)], 1, 2),
             road("on", [(600.0, 0.0), (900.0, 0.0)], 2, 3)]
    nodes, ways = crossing_at(320.0)
    # A slow walker, so the car comes before it is over
    traffic = traffic_with(roads, [car("car", "north", 40.0, speed_kph=40)], nodes, ways, [(0, 0.0, 0.5)], engine=engine)
    start, _ = band_of(traffic, "east|1")
    turning, walker = vehicle(traffic, "car"), traffic.walkers[0]
    stood = False
    for i in range(round(40 / DT)):
        traffic.step(DT, (i + 1) * DT)
        assert traffic.pedestrian_hits() == []
        if on_road_part(walker) and turning.road.road_id == "east":
            # Out of the crossing while the walker is in its lane: on the road it turned into, it goes once the
            # walker is past its lane (per-lane release)
            assert (turning.distance + turning.half < start or turning.distance - turning.half > start + 3.5
                    or abs(walker.y) >= clear_of(turning))
            stood = stood or turning.speed < GAVE_WAY_MPS
    assert stood
    assert turning.road.road_id != "north"


def test_a_shuttle_stops_short_of_a_crossing_a_walker_is_on(engine):
    nodes, ways = crossing_at(300.0)
    traffic = traffic_with(EAST, [], nodes, ways, [(0, 0.0, 1.0)], engine=engine)
    traffic.step(DT, DT)
    assert traffic.walkers[0].state == ON_CROSSING
    start, _ = band_of(traffic, "east|1")
    route = ShuttleRoute([(0.0, 0.0), (600.0, 0.0)])
    shuttle = traffic.shuttle("shuttle-01", 6.0, 1.3)
    traffic.place_shuttle(shuttle, route, 280.0, 8.0, 0.05, in_traffic=True)
    plan = traffic.plan_shuttle(shuttle, route, 280.0, 10.0)
    assert plan.held and plan.cap_m == pytest.approx(start - 6.0 - city_traffic.STOP_MARGIN_M - 280.0)
    assert plan.limit_mps < 8.0
    # The walker is what holds it up, and the plan says so
    assert plan.pedestrian_mps == plan.limit_mps
    # At the place it stops it may go no further, and no faster than standing
    stop_at = start - 6.0 - city_traffic.STOP_MARGIN_M
    shuttle.speed = 0.0
    plan = traffic.plan_shuttle(shuttle, route, stop_at, 10.0)
    assert plan.cap_m == pytest.approx(0.0, abs=1e-9) and plan.limit_mps == pytest.approx(0.0, abs=1e-9)


# ---------------------------------------------------------------- the whole area

def test_on_toa_payoh_walkers_cross_signals_only_on_red_and_nothing_touches_them(engine):
    traffic = engine.start("toa-payoh", pedestrians=True)
    junction = next(c.junction for c in traffic.walks.crossings if c.control == walk_network.SIGNAL)
    # A fault sticks one junction's lights on red: its crossings become crossings without priority
    traffic.set_signal_fault(junction, 60.0)
    granted = {w.slot: w.granted for w in traffic.walkers}
    started = 0
    for i in range(1500):
        time_s = (i + 1) * DT
        traffic.step(DT, time_s)
        for walker in traffic.walkers:
            if walker.granted != -1 and granted[walker.slot] != walker.granted:
                crossing = traffic.walks.ways[walker.granted].crossing
                if crossing.control == walk_network.SIGNAL and not traffic._fault(crossing.junction):
                    assert light(time_s, crossing.offset, crossing.phase) == 2
                    started += 1
            granted[walker.slot] = walker.granted
        if i % 5 == 4:
            assert traffic.pedestrian_hits() == []
    report = traffic.report()
    assert started > 10
    assert report["pedestrian_hits"] == [] and report["overlaps"] == 0
    assert report["walkers_walking"] + report["walkers_waiting"] + report["walkers_crossing"] + report["walkers_away"] == 600
    assert report["walkers_away"] < 600 * 0.3, "most are out at 8 in the morning"
    assert 0.8 < report["walker_mean_speed_mps"] < 1.6


def test_walkers_survive_a_checkpoint_and_a_staged_copy_exactly(engine):
    traffic = engine.start("toa-payoh", pedestrians=True)
    for i in range(100):
        traffic.step(DT, (i + 1) * DT)
    state = json.dumps(traffic.state())
    restored = engine.from_state(json.loads(state))
    assert json.dumps(restored.state()) == state
    copy = deepcopy(traffic)
    for i in range(100, 200):
        copy.step(DT, (i + 1) * DT)
    assert json.dumps(traffic.state()) == state, "the staged copy changed the original"
    for i in range(100, 200):
        traffic.step(DT, (i + 1) * DT)
        restored.step(DT, (i + 1) * DT)
    assert restored.state() == traffic.state() == copy.state()


def test_a_checkpoint_from_before_pedestrians_starts_them_from_their_seeds(engine):
    without = engine.start("toa-payoh", pedestrians=False)
    without.step(DT, DT)
    restored = engine.from_state(without.state(), without.network, walk_network.walk_network("toa-payoh"))
    fresh = engine.start("toa-payoh", pedestrians=True)
    assert restored.state()["walkers"] == fresh.state()["walkers"]
    # One whose walkers do not match the network is not trusted
    for damage in ("count", "source", "crossing"):
        state = fresh.state()
        if damage == "count":
            state["walkers"] = state["walkers"][:-1]
        elif damage == "source":
            state["walk_source"] = "another"
        else:
            state["crossings"][0] = [len(fresh.walks.crossings)]
        with pytest.raises(ValueError):
            engine.from_state(state, fresh.network, fresh.walks)


def test_packed_walkers_are_where_the_traffic_has_them(engine):
    traffic = engine.start("toa-payoh", pedestrians=True)
    for i in range(50):
        traffic.step(DT, (i + 1) * DT)
    packed, count = traffic.pedestrian_frame()
    # Only the walkers out: those indoors or away on a bus are not drawn
    out = [walker for walker in traffic.walkers if walker.state != city_traffic.AWAY]
    assert 0 < count == len(out) < 600 and len(packed) == count * PED_RECORD.size == count * 14
    for (seed, x, y, heading, speed, flags), walker in zip(PED_RECORD.iter_unpack(packed), out):
        assert seed == walker.seed
        assert abs(x / 100 - walker.x) <= 0.005 + 1e-9 and abs(y / 100 - walker.y) <= 0.005 + 1e-9
        assert abs(heading / 10 - math.degrees(math.atan2(walker.fy, walker.fx))) <= 0.05
        assert abs(speed / 50 - walker.speed) <= 0.01 + 1e-9
        assert bool(flags & city_traffic.ON_ROAD_FLAG) == on_road_part(walker)
        assert bool(flags & city_traffic.WAITING_FLAG) == (walker.state == WAITING_AT_KERB)


# ---------------------------------------------------------------- the live session

@pytest.mark.traffic
def test_a_session_with_pedestrians_sends_them_and_never_runs_one_over(monkeypatch, engine):
    monkeypatch.setenv(walk_network.PEDESTRIANS_VARIABLE, "1")
    monkeypatch.setenv(native_traffic.ENGINE_VARIABLE, "1" if engine is NativeCityTraffic else "0")
    actor = IntegratedSessionActor(0, session_id="pedestrian-test")
    assert type(actor.traffic) is engine and len(actor.traffic.walkers) == 600
    actor.is_paused = False
    actor.modes = {s.ID: "IN_SERVICE" for s in actor.shuttles}
    # A signal fault on the junctions of the first few signalled crossings, as a world event would
    for junction in sorted({c.junction for c in actor.traffic.walks.crossings if c.control == walk_network.SIGNAL})[:5]:
        actor.traffic.set_signal_fault(junction, 120.0)
    frames, held_for_walkers = 0, 0
    for tick in range(6000):  # Five minutes
        actor.tick()
        # Shuttles stop for the walkers at the crossings on their routes, and say so
        held_for_walkers += sum(1 for reason in actor.hold_reasons.values() if reason == "pedestrians")
        if tick % 10 == 0:
            # A shuttle's 4.6 m body reaches over the kerb from a kerb lane, and a long truck's round a
            # bend, over a walker on the footpath now and then: the rectangles' shape, counted apart
            # (CityTraffic.pedestrian_hits)
            hits, overhangs = actor.traffic._walker_contacts()
            assert hits == []
            assert all(not on_road_part(w) for _, walker_id in overhangs for w in actor.traffic.walkers if w.id == walker_id)
            assert actor.traffic.shuttle_overlaps() == []
        if actor.traffic_frame_due() and actor.current_tick % 200 == 0:
            frame = actor.traffic_frame()
            StreamTrafficFrame.model_validate(frame)
            packed = base64.b64decode(frame["pedestrians"])
            out = sum(1 for walker in actor.traffic.walkers if walker.state != city_traffic.AWAY)
            assert frame["pedestrian_count"] == out and len(packed) == out * PED_RECORD.size
            # A shuttle the traffic holds says what for, as the actor last placed it
            assert {s["vehicle_id"]: s["held_by"] for s in frame["shuttles"] if "held_by" in s} == actor.hold_reasons
            frames += 1
    assert frames > 0
    assert held_for_walkers > 0, "a shuttle stopped for the walkers on a crossing on its route"
    report = actor.traffic.report()
    assert report["red_light_runs"] == 0 and report["overlaps"] == 0 and report["pedestrian_hits"] == []
    moved = sum(1 for w, seed in zip(actor.traffic.walkers, actor.traffic.walks.seeds) if w.way is not seed[2])
    assert moved > 300, "the walkers walk"
