"""
Tests for phase 5 of sidewalks_plan.md in the backend's traffic: per-lane release at crossings, walkers'
trips between attractors and how many are out by the time of day, and the "pedestrian on the road" world
event (a jaywalker the traffic and the shuttles must stop for).

Description:
    On small made-up roads and walkways: on a two-way road, the car in the lane a walker has passed goes
    before the walker is off the crossing while the car in the lane ahead of it waits; a trip follows the
    shortest way, a crossing counting as the wait it usually costs; walkers go to their attractors, go in
    and come out again; a jaywalker waits for a car that could not stop for it, steps out in front of one
    that can (which stops), stands a while in the middle of the road and steps onto the far sidewalk; one
    whose event ends before it goes walks back; a shuttle stops for it and says why. On Toa Payoh: as many
    walkers are out as the hour has, and a scheduled pedestrian event in a live session sends one across
    its road. Each runs on both traffic engines where it can; test_native_traffic holds them to each other.
"""

import json
import math
from copy import deepcopy

import pytest

from backend.tests.test_city_pedestrians import EAST, MODELS, car, crossing_at, engine, road  # noqa: F401
from backend.tests.test_random_incidents import running_actor
from src.services.incident_scenarios.world_events import ScheduledEvent, load_timeline
from src.traffic import city_traffic, walk_network
from src.traffic.city_traffic import AWAY, CROSS, DONE, HESITATE, JAYWALKING, KERB, CityTraffic, ShuttleRoute
from src.traffic.native_traffic import NativeCityTraffic
from src.traffic.road_network import RoadNetwork
from src.traffic.walk_network import WalkNetwork

DT = 0.1


def walks(network, nodes, ways, seeds, attractors=(), hourly=None):
    data = {"area": "test", "source_hash": "test-trips",
            "nodes": [{"id": i, "xy_m": list(p), "kind": "joint"} for i, p in enumerate(nodes)],
            "walkways": [dict(way, id=i) for i, way in enumerate(ways)],
            "seeds": [{"id": f"ped-{i:04d}", "walkway": walkway, "distance_m": distance, "speed_mps": speed,
                       "height_m": 1.7, "model": 0} for i, (walkway, distance, speed) in enumerate(seeds)],
            "attractors": [{"id": f"place-{i}", "kind": kind, "node": node, "weight": 1.0}
                           for i, (kind, node) in enumerate(attractors)]}
    if hourly is not None:
        data["population"] = {"hourly": hourly}
    return WalkNetwork("test", data, network, ())


def traffic_on(roads, cars, nodes, ways, seeds, engine, attractors=(), hourly=None, day_start_s=8 * 3600.0):
    network = RoadNetwork("test", {"models": MODELS, "vehicles": cars, "roads": roads}, ())
    traffic = engine.on(network, walks(network, nodes, ways, seeds, attractors, hourly), day_start_s)
    assert type(traffic) is engine
    return traffic


def sidewalk(a, b, points, width=1.8):
    return {"kind": "sidewalk", "from": a, "to": b, "points_m": [list(p) for p in points], "width_m": width, "lanes": 2}


def run(traffic, seconds, start_s=0.0, every=None):
    steps = round(seconds / DT)
    for i in range(steps):
        traffic.step(DT, start_s + (i + 1) * DT)
        assert traffic.pedestrian_hits() == []
        if every is not None:
            every(start_s + (i + 1) * DT)
    return start_s + steps * DT


def vehicle(traffic, identifier):
    return next(v for v in traffic.vehicles if v.id == identifier)


# ---------------------------------------------------------------- per-lane release

def test_on_a_two_way_road_the_lane_a_walker_has_passed_goes_and_the_one_ahead_waits(engine):
    """A slow walker over a two-way street with a car coming each way: the car whose lane the walker is past
    drives on while the walker is still on the road; neither is ever in the crossing while the walker is in
    its lane."""
    roads = [road("street", [(0.0, 0.0), (600.0, 0.0)], 1, 2, oneway=False)]
    nodes, ways = crossing_at(300.0, y_road=3.8, y_node=4.2)
    westbound = dict(car("west", "street", 0.0, speed_kph=40), direction=-1, distance_m=240.0)
    traffic = traffic_on(roads, [car("east", "street", 240.0, speed_kph=40), westbound], nodes, ways,
                         [(0, 0.0, 0.5)], engine)
    by_road = traffic.walks.by_road
    bands = {key: (lambda entry: (entry[0] - entry[2], entry[0] + entry[2]))(by_road[traffic.network.by_key[key].index][0])
             for key in ("street|1", "street|-1")}
    walker = traffic.walkers[0]
    # Where across the road the walker was when each car first drove into the crossing with it on the road
    went_while_crossing: dict[str, float] = {}

    def check(_):
        on_road = walker.way.crossing is not None and walker.way.kerb_in <= walker.distance <= walker.way.kerb_out
        for identifier, key in (("east", "street|1"), ("west", "street|-1")):
            car_now = vehicle(traffic, identifier)
            start, end = bands[key]
            if car_now.road.key != key or not (car_now.distance + car_now.half > start and car_now.distance - car_now.half < end):
                continue
            clear = car_now.half_width + city_traffic.LANE_RELEASE_MARGIN_M + walk_network.WALKER_HALF_M - 1e-9
            if on_road:
                assert abs(walker.y - car_now.y) >= clear, f"{identifier} drove into the walker's lane"
                went_while_crossing.setdefault(identifier, walker.y)

    run(traffic, 40, every=check)
    assert went_while_crossing, "a car went before the walker was over"
    # The first went while the walker was yet to clear the other lane (1.6 m the other side of the line,
    # cleared at 1.65 m from it): it was let go for its own lane alone
    first = min(went_while_crossing.values(), key=abs)
    assert abs(first) < 1.6 + 1.65


# ---------------------------------------------------------------- trips

def test_a_trip_takes_the_shortest_way_a_crossing_counting_as_its_wait():
    """From node 0 to node 3 either over a signalled crossing (8 m) or round by the sidewalks; the detour is
    taken while it is shorter than the crossing's usual wait at a walk, and not once it is longer."""
    network = RoadNetwork("test", {"models": MODELS, "vehicles": [], "roads": EAST}, ())

    def tree(detour_m):
        nodes = [(300.0, 2.6), (300.0, -2.6), (300.0 + detour_m / 2, 2.6), (300.0, -2.6 - 1e-3)]
        ways = [{"kind": "crossing", "from": 0, "to": 1, "points_m": [[300.0, 2.6], [300.0, 2.2], [300.0, -2.2], [300.0, -2.6]],
                 "width_m": 3.0, "lanes": 4, "crossing_id": "c", "control": "zebra"},
                sidewalk(0, 2, [nodes[0], nodes[2]]), sidewalk(2, 1, [nodes[2], nodes[1]])]
        built = walks(network, nodes, ways, [])
        hop = built.next_hop(1)[0]
        return built.ways[hop].kind
    # The two sidewalks' length less the crossing's is about the detour; a zebra counts 6.5 m more than its length
    assert tree(2.0) == walk_network.SIDEWALK
    assert tree(20.0) == walk_network.CROSSING


def test_the_share_out_follows_the_hourly_curve_from_one_hour_to_the_next():
    network = RoadNetwork("test", {"models": MODELS, "vehicles": [], "roads": EAST}, ())
    hourly = [0.1 * (h % 10) for h in range(24)]
    built = walks(network, [(0.0, 5.0), (10.0, 5.0)], [sidewalk(0, 1, [(0.0, 5.0), (10.0, 5.0)])], [], hourly=hourly)
    assert built.density(3 * 3600) == pytest.approx(0.3)
    assert built.density(3.5 * 3600) == pytest.approx(0.35)
    assert built.density(23.5 * 3600) == pytest.approx(0.15), "the last hour runs on to midnight's"
    assert built.density(-0.5 * 3600 + 86400) == built.density(23.5 * 3600)
    assert walks(network, [(0.0, 5.0), (10.0, 5.0)], [sidewalk(0, 1, [(0.0, 5.0), (10.0, 5.0)])], []).density(0) == 1.0


def test_walkers_go_where_their_trips_are_to_go_in_and_come_out_again(engine):
    """A street of sidewalks between a home at one end and a bus stop at the other: every walker sets off for
    one of them, gets there, goes in (or leaves on a bus) or sets off again, and comes back out."""
    nodes = [(0.0, 5.0), (50.0, 5.0), (100.0, 5.0), (150.0, 5.0)]
    ways = [sidewalk(i, i + 1, [nodes[i], nodes[i + 1]]) for i in range(3)]
    seeds = [(1, 10.0 + 4 * i, 1.2 + 0.05 * i) for i in range(6)]
    traffic = traffic_on(EAST, [], nodes, ways, seeds, engine, attractors=[("home", 0), ("bus_stop", 3)])
    assert all(w.dest in (0, 1) for w in traffic.walkers), "every walker starts on a trip"
    arrived, went_in, came_out = set(), set(), set()
    away_before = {w.slot: False for w in traffic.walkers}

    def watch(_):
        for w in traffic.walkers:
            if w.home >= 0:
                arrived.add(w.slot)
            if w.state == AWAY:
                went_in.add(w.slot)
            elif away_before[w.slot]:
                came_out.add(w.slot)
            away_before[w.slot] = w.state == AWAY
            if w.state != AWAY:
                assert 0.0 <= w.x <= 150.0 and abs(w.y - 5.0) < 1.0, "on the street's sidewalks"

    run(traffic, 900, every=watch)
    assert len(arrived) == len(seeds) and went_in and came_out
    report = traffic.report()
    assert report["walkers_on_trips"] + report["walkers_away"] >= 1


def test_on_toa_payoh_as_many_walkers_are_out_as_the_hour_has(engine):
    night = engine.start("toa-payoh", pedestrians=True, day_start_s=3 * 3600.0)
    evening = engine.start("toa-payoh", pedestrians=True, day_start_s=18 * 3600.0)
    walks_network = night.walks
    for traffic, hour in ((night, 3), (evening, 18)):
        share = walks_network.density(hour * 3600.0)
        out = sum(1 for w in traffic.walkers if w.state != AWAY)
        assert abs(out - share * 600) < 0.1 * 600, f"about {share:.0%} out at {hour}:00"
        packed, count = traffic.pedestrian_frame()
        assert count == out
    for i in range(1200):
        night.step(0.5, (i + 1) * 0.5)
    target = int(600 * walks_network.density(3 * 3600.0 + 600.0))
    out = sum(1 for w in night.walkers if w.state != AWAY)
    assert abs(out - target) <= 5, "the walkers out follow the hour"
    assert night.report()["pedestrian_hits"] == []


def test_walkers_on_trips_survive_a_checkpoint_and_engines_agree_on_it():
    python = CityTraffic.start("toa-payoh", pedestrians=True, day_start_s=12 * 3600.0)
    for i in range(200):
        python.step(0.5, (i + 1) * 0.5)
    state = json.loads(json.dumps(python.state()))
    assert state["day_start_s"] == 12 * 3600.0 and all(len(item) == 14 for item in state["walkers"])
    restored = CityTraffic.from_state(state)
    assert restored.state() == python.state()
    # A checkpoint from before trips: its walkers have no trip and are at no attractor
    old = deepcopy(state)
    old["walkers"] = [item[:12] for item in old["walkers"]]
    del old["day_start_s"]
    assert all(w.dest == -1 and w.home == -1 for w in CityTraffic.from_state(old).walkers)
    bad = deepcopy(state)
    bad["walkers"][0][12] = len(python.walks.attractors)
    with pytest.raises(ValueError):
        CityTraffic.from_state(bad)


def test_a_checkpoint_naming_a_walkway_it_does_not_have_is_refused_by_either_engine(engine):
    """A negative walkway index is refused as one past the end is, not wrapped round to another walkway."""
    traffic = CityTraffic.start("toa-payoh", pedestrians=True)
    state = json.loads(json.dumps(traffic.state()))
    count = len(traffic.walks.ways)
    for way, ahead in ((-1, None), (count, None), (None, [-1]), (None, [count])):
        bad = deepcopy(state)
        if way is not None:
            bad["walkers"][0][0] = way
        if ahead is not None:
            bad["walkers"][0][11] = ahead
        with pytest.raises(IndexError):
            engine.from_state(bad)


def test_a_malformed_day_start_stops_only_traffic_that_walks_someone(engine, monkeypatch):
    monkeypatch.setenv(walk_network.DAY_START_VARIABLE, "noon")
    traffic = engine.start("toa-payoh", pedestrians=False)
    assert traffic is not None and traffic.vehicles, "traffic without pedestrians never reads F9_DAY_START"
    with pytest.raises(ValueError):
        engine.start("toa-payoh", pedestrians=True)


def test_engines_agree_on_who_a_jaywalk_may_send():
    python = CityTraffic.start("toa-payoh", pedestrians=True)
    for i in range(100):
        python.step(0.5, (i + 1) * 0.5)
    native = NativeCityTraffic.from_state(json.loads(json.dumps(python.state())))
    if not isinstance(native, NativeCityTraffic):
        pytest.skip("native traffic engine unavailable")
    candidates = list(python.jaywalk_candidates())
    assert candidates and candidates == list(native.jaywalk_candidates())


# ---------------------------------------------------------------- the jaywalker

def jaywalk_street(engine, cars=(), speed=1.0):
    """The east road with a sidewalk each side (y = 5 north, -5 south) and one walker on the north one, at
    x = 300 whichever way it walks."""
    nodes = [(250.0, 5.0), (350.0, 5.0), (250.0, -5.0), (350.0, -5.0)]
    ways = [sidewalk(0, 1, [nodes[0], nodes[1]]), sidewalk(2, 3, [nodes[2], nodes[3]])]
    traffic = traffic_on(EAST, list(cars), nodes, ways, [(0, 50.0, speed)], engine)
    return traffic, traffic.network.by_key["east|1"]


def phase_of(traffic, key):
    jaywalk = traffic.jaywalks.get(key)
    if jaywalk is None:
        return None
    return jaywalk["phase"] if isinstance(jaywalk, dict) else jaywalk.phase


def on_road(traffic, walker):
    return walker.state == JAYWALKING and abs(walker.y) < 2.2


def test_a_jaywalker_steps_out_in_front_of_a_car_that_can_stop_and_it_does(engine):
    traffic, east = jaywalk_street(engine, [car("car", "east", 220.0, speed_kph=40)])
    walker = traffic.walkers[0]
    assert traffic.jaywalk("jay", east, 300.0, until_s=90.0)
    assert traffic.jaywalk("jay", east, 100.0, until_s=10.0), "sending it again changes nothing"
    assert walker.state == JAYWALKING
    seen, car_stopped, stood = set(), False, False

    def watch(_):
        nonlocal car_stopped, stood
        seen.add(phase_of(traffic, "jay"))
        moving = vehicle(traffic, "car")
        if moving.x + moving.half < 300.0 - city_traffic.JAYWALK_HALF_WIDTH_M and moving.speed < 0.3:
            car_stopped = True
        if on_road(traffic, walker):
            assert not moving.x + moving.half > 300.0 - 1.0 or moving.x - moving.half > 301.0, "never over the jaywalker"
            stood = stood or walker.speed == 0.0

    run(traffic, 60, every=watch)
    assert {KERB, CROSS, HESITATE, DONE} <= seen and car_stopped and stood
    assert walker.state != JAYWALKING and walker.way.ys[0] < 0, "it stepped onto the far sidewalk"
    run(traffic, 40, 60.0)
    assert "jay" not in traffic.jaywalks, "forgotten once its event is over"
    assert vehicle(traffic, "car").x > 310.0, "and the car went on"


def test_a_jaywalker_waits_for_a_car_that_could_not_stop_for_it(engine):
    # The car is too close to stop for it by the time the jaywalker is at the kerb
    traffic, east = jaywalk_street(engine, [car("near", "east", 250.0, speed_kph=50)])
    assert traffic.jaywalk("jay", east, 300.0, until_s=90.0)
    near = vehicle(traffic, "near")
    slowest, waited = math.inf, 0.0

    def watch(_):
        nonlocal slowest, waited
        if near.x - near.half < 302.0:
            slowest = min(slowest, near.speed)
            waited += DT if phase_of(traffic, "jay") == KERB else 0.0

    run(traffic, 30, every=watch)
    assert waited > 0.2, "the jaywalker stood at the kerb while the car came"
    assert slowest > 10.0, "and the car never had to brake for it"
    assert phase_of(traffic, "jay") == DONE


def test_a_jaywalker_whose_event_ends_before_it_goes_walks_back(engine):
    traffic, east = jaywalk_street(engine)
    walker = traffic.walkers[0]
    assert traffic.jaywalk("jay", east, 300.0, until_s=1.0)

    def watch(_):
        assert not on_road(traffic, walker)

    run(traffic, 20, every=watch)
    assert walker.state != JAYWALKING and walker.way.ys[0] > 0, "back on its own sidewalk"
    assert "jay" not in traffic.jaywalks


def test_a_jaywalker_needs_a_walker_near_and_somewhere_to_land():
    traffic, east = jaywalk_street(CityTraffic)
    assert not traffic.jaywalk("far", east, 100.0, until_s=90.0), "no walker within reach"
    assert not traffic.jaywalk("over", east, 300.0, until_s=0.0), "an event already over"
    assert "far" not in traffic.jaywalks and "over" not in traffic.jaywalks


def test_a_shuttle_stops_for_a_jaywalker_on_its_route_and_says_why(engine):
    traffic, east = jaywalk_street(engine)
    route = ShuttleRoute([(0.0, 0.0), (600.0, 0.0)])
    shuttle = traffic.shuttle("shuttle-01", 6.0, 1.3)
    # Coming at 8 m/s, close enough to have to brake for it, far enough to stop
    traffic.place_shuttle(shuttle, route, 275.0, 8.0, 0.05, in_traffic=True)
    assert traffic.jaywalk("jay", east, 300.0, until_s=90.0)
    clock = 0.0
    while phase_of(traffic, "jay") not in (CROSS, HESITATE):
        clock = run(traffic, DT, clock)
        assert clock < 20.0, "the jaywalker stepped out in front of a standing shuttle"
    plan = traffic.plan_shuttle(shuttle, route, 275.0, 10.0)
    assert plan.held and plan.pedestrian_mps == plan.limit_mps < 10.0
    assert plan.cap_m < 300.0 - city_traffic.JAYWALK_HALF_WIDTH_M - 6.0 - 275.0


def test_a_jaywalk_survives_a_checkpoint_in_either_engine(engine):
    traffic, east = jaywalk_street(engine, [car("car", "east", 200.0, speed_kph=40)])
    assert traffic.jaywalk("jay", east, 300.0, until_s=90.0)
    clock = run(traffic, 5.0)
    state = json.loads(json.dumps(traffic.state()))
    assert [item[0] for item in state["jaywalks"]] == ["jay"]
    for reader in (CityTraffic, NativeCityTraffic if engine is NativeCityTraffic else CityTraffic):
        restored = reader.from_state(state, traffic.network, traffic.walks)
        assert restored.state() == state
        twin = deepcopy(traffic)
        run(twin, 10.0, clock)
        run(restored, 10.0, clock)
        assert restored.state() == twin.state()
    assert traffic.state() == state, "the staged copy left the original alone"


# ---------------------------------------------------------------- the world event

def test_a_timeline_can_schedule_a_pedestrian_on_the_road():
    rows = [{"event_id": "jay", "event_type": "PEDESTRIAN_ON_ROAD", "location": {"edge_id": "road-1"}, "at_s": 5,
             "duration_s": 60}]
    [event] = load_timeline(rows, edges={"road-1"})
    assert event.event_type == "PEDESTRIAN_ON_ROAD" and event.place(0.05).kind == "ROAD_ACCIDENT"


@pytest.mark.traffic
@pytest.mark.random_opening
def test_a_scheduled_pedestrian_on_the_road_sends_a_walker_across_it(monkeypatch):
    monkeypatch.setenv(walk_network.PEDESTRIANS_VARIABLE, "1")
    actor = running_actor(2)
    assert actor.traffic is not None and actor.traffic.walks is not None
    edges = actor.definition.network["edges"]
    spots = [(w.x, w.y) for w in actor.traffic.walkers if w.state == 0 and w.way.crossing is None]

    # The edge whose road has the most walkers near its middle, so one is sure to be in reach
    def walkers_near(edge):
        place = actor.traffic.locate([tuple(p) for p in edges[edge]["path"]])
        if place is None:
            return 0
        x, y, _, _ = place[0].sample(place[1], 0.0)
        return sum(1 for wx, wy in spots if math.hypot(wx - x, wy - y) < 25.0)
    edge = max(sorted(edges), key=walkers_near)
    assert walkers_near(edge) > 0
    at = (actor.current_tick + 1) * actor.STEP_S
    monkeypatch.setattr(actor.incident_catalog, "timeline", [ScheduledEvent("jay", "PEDESTRIAN_ON_ROAD", edge, at, 60.0)])
    for _ in range(80):
        actor.tick()
    record = next(r for r in actor.random_incidents if r["incident_id"] == "scheduled-jay")
    assert record["kind"] == "ROAD_ACCIDENT" and record["congested_edge_ids"] == [edge]
    assert "jay" in actor.traffic.jaywalks
    # The traffic keeps off no stretch of road for it: the walker is what it stops for
    assert not [h for h in actor.traffic.hazards.values()]
