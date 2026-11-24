"""
Tests for the native background traffic engine (backend/native/traffic, src/traffic/native_traffic.py).

Description:
    The native engine must drive exactly as src/traffic/city_traffic.py does, bit for bit, so either
    engine continues the other's checkpoints and a session's state never depends on which one ran it. These
    hold the two to each other: CPython's float arithmetic as the native code reproduces it, the tuning
    constants, the area's traffic over minutes of steps, whole integrated sessions with their incidents
    staged as the session service stages them, checkpoints crossing between the engines, and sessions
    stepping on several threads at once with the GIL let go. The engine choice (F9_NATIVE_TRAFFIC) and
    the refusal of a module built from older source are tested too. test_city_traffic and
    test_traffic_events run their own tests on both engines as well (conftest.traffic_engine).

    Everything but the engine choice is skipped where the native module is not built
    (python Tools/build_native.py).
"""

import json
import math
import random
import struct
import threading
from copy import deepcopy

import pytest

from src.services.integrated_actor import IntegratedSessionActor
from src.traffic import native_traffic
from src.traffic.city_traffic import CityTraffic, ShuttleRoute
from src.traffic.native_traffic import NativeCityTraffic
from src.traffic.road_network import angle_deg

native = pytest.mark.skipif(not native_traffic.status()[0], reason=f"native traffic engine: {native_traffic.status()[1]}")
DT = 0.1


def bits(value: float) -> bytes:
    return struct.pack("<d", value)


def run(traffic, steps, start_s=0.0):
    for i in range(steps):
        traffic.step(DT, start_s + (i + 1) * DT)
    return start_s + steps * DT


def without_event_ids(value):
    """Event ids are random UUIDs in either engine."""
    if isinstance(value, dict):
        return {k: without_event_ids(v) for k, v in value.items() if k != "event_id"}
    if isinstance(value, list):
        return [without_event_ids(v) for v in value]
    return value


# ---------------------------------------------------------------- arithmetic and constants

@native
def test_the_native_float_arithmetic_rounds_exactly_as_cpython_does():
    from src.traffic import _traffic_native
    rng = random.Random(9)

    def value():
        pick = rng.random()
        if pick < 0.4:
            return rng.uniform(-1000.0, 1000.0)
        if pick < 0.7:
            return rng.uniform(-1.0, 1.0) * 10 ** rng.uniform(-12, 12)
        if pick < 0.85:
            raw = struct.unpack("<d", struct.pack("<Q", rng.getrandbits(64)))[0]
            return raw if math.isfinite(raw) else 1.0
        return rng.choice([0.0, -0.0, 1.0, -1.0, 2.0, 1e-310, -5e-324, 1e300, 70.0])

    a = [value() for _ in range(40000)]
    b = [value() for _ in range(40000)]
    # Nearly equal coordinates, as math.dist sees most of them
    a += [rng.uniform(-500, 500) for _ in range(10000)]
    b += [x + rng.uniform(-1e-6, 1e-6) for x in a[-10000:]]
    expected = {
        "hypot": [math.hypot(x, y) for x, y in zip(a, b)],
        "square": [x ** 2 if abs(x) < 1e150 else 0.0 for x in a],
        "mod": [x % y if y else 0.0 for x, y in zip(a, b)],
        "atan2": [math.atan2(x, y) for x, y in zip(a, b)],
        "degrees": [math.degrees(x) for x in a],
    }
    for op, want in expected.items():
        got = _traffic_native.math_check(op, a, b)
        mismatched = [(x, y) for x, y, w, g in zip(a, b, want, got)
                      if not (op == "square" and abs(x) >= 1e150) and not (op == "mod" and not y) and bits(w) != bits(g)]
        assert not mismatched, f"{op} differs from CPython at {mismatched[:3]}"
    unit = [rng.uniform(-1.0, 1.0) for _ in range(20000)]
    assert [bits(math.acos(x)) for x in unit] == [bits(v) for v in _traffic_native.math_check("acos", unit, unit)]
    assert ([bits(angle_deg(x, y, y, x)) for x, y in zip(a[-10000:], b[-10000:])]
            == [bits(v) for v in _traffic_native.math_check("angle", a[-10000:], b[-10000:])])
    for _ in range(20000):
        weights = [max(0.01, rng.uniform(0.0, 8.0) * rng.uniform(0.4, 1.6)) for _ in range(rng.randint(2, 6))]
        assert bits(sum(weights)) == bits(_traffic_native.py_sum(weights))


@native
def test_the_native_constants_are_city_traffics():
    assert native_traffic.mismatched_constants() == []


# ---------------------------------------------------------------- the traffic

@native
def test_the_area_traffic_drives_identically_on_both_engines():
    python, fast = CityTraffic.start("toa-payoh"), NativeCityTraffic.start("toa-payoh")
    assert fast.state() == python.state()
    clock = 0.0
    for _ in range(12):
        clock = run(python, 50, clock)
        run(fast, 50, clock - 50 * DT)
        assert fast.state() == python.state()
        assert fast.frame() == python.frame()
    assert fast.report() == python.report()
    assert json.dumps(fast.state()) == json.dumps(python.state())


@native
def test_hazards_pins_and_signal_faults_play_out_identically():
    python, fast = CityTraffic.start("toa-payoh"), NativeCityTraffic.start("toa-payoh")
    clock = 0.0
    for traffic in (python, fast):
        run(traffic, 30)
    clock = 30 * DT
    network = python.network
    # A closure along a busy road, a breakdown on another and a junction's lights stuck on red
    busiest = sorted(network.roads, key=lambda r: -sum(1 for v in python.vehicles if v.road is r and not v.outside))
    closed, broken = busiest[0], busiest[3]
    line = [(closed.xs[0], closed.ys[0]), (closed.xs[-1], closed.ys[-1])]
    junction = next(stop[3] for road in network.roads for stop in road.stops)
    for traffic in (python, fast):
        traffic.set_hazards([(line, False)])
        assert traffic.pin_vehicle("stall", broken, broken.length / 2, 0, clock + 20.0, 2)
        traffic.set_signal_fault(junction, clock + 15.0)
    assert fast.state() == python.state()
    assert fast.hazards == python.hazards and fast.pins == python.pins and fast.signal_faults == python.signal_faults
    for _ in range(6):
        clock = run(python, 50, clock)
        run(fast, 50, clock - 50 * DT)
        assert fast.state() == python.state()
    assert fast.pins == python.pins == {} and fast.signal_faults == python.signal_faults == {}


@native
def test_the_area_traffic_without_pedestrians_drives_identically_on_both_engines():
    python, fast = CityTraffic.start("toa-payoh", pedestrians=False), NativeCityTraffic.start("toa-payoh", pedestrians=False)
    assert fast.walkers == python.walkers == [] and "walkers" not in fast.state()
    run(python, 200)
    run(fast, 200)
    assert fast.state() == python.state() and fast.report() == python.report()


def _shuttle_route(traffic):
    """A route along the roads with the most crossings on them, joined end to end as a driver would."""
    network, walks = traffic.network, traffic.walks
    road = max(network.roads, key=lambda r: (len(walks.by_road[r.index]), -r.index))
    points = list(zip(road.xs, road.ys))
    while len(points) < 60:
        leaving = [r for r in network.exits.get(road.to_node, ()) if r.road_id != road.road_id] or \
            list(network.exits.get(road.to_node, ()))
        if not leaving:
            break
        road = max(leaving, key=lambda r: (len(walks.by_road[r.index]), -r.index))
        points += list(zip(road.xs, road.ys))[1:]
        if sum(math.dist(a, b) for a, b in zip(points, points[1:])) > 1500:
            break
    return points


def _drive_shuttle(traffic, route, state, dt):
    """A shuttle driven along a route as the session drives one: what the traffic allows, never further
    than it may go; the plan it was given."""
    shuttle = traffic.shuttle("shuttle-01", 6.0, 1.3)
    plan = traffic.plan_shuttle(shuttle, route, state["along"], 10.0)
    target = min(10.0, plan.limit_mps, plan.bend_mps)
    speed = min(target, state["speed"] + 1.5 * dt) if target > state["speed"] else max(target, state["speed"] - 9.0 * dt)
    step = max(0.0, min(speed * dt, plan.cap_m, route.length - state["along"] - 1.0))
    state["along"] += step
    state["speed"] = step / dt
    traffic.place_shuttle(shuttle, route, state["along"], state["speed"], dt, held_by_traffic=plan.held, in_traffic=True)
    return (plan.limit_mps, plan.cap_m, plan.held, plan.bend_mps, plan.pedestrian_mps)


@native
@pytest.mark.traffic
def test_walkers_walk_identically_on_both_engines_through_faults_incidents_and_a_shuttle():
    """The phase 4 bar of sidewalks_plan.md: native and Python walkers agree bit for bit over 3,000 steps,
    with signal faults, a closure, a breakdown and a shuttle giving way at the crossings on its route."""
    python, fast = CityTraffic.start("toa-payoh", pedestrians=True), NativeCityTraffic.start("toa-payoh", pedestrians=True)
    assert len(fast.walkers) == len(python.walkers) == 600
    assert fast.state() == python.state()
    network = python.network
    points = _shuttle_route(python)
    routes = {"python": ShuttleRoute(points), "native": ShuttleRoute(points)}
    assert fast.walks.route_crossings(routes["native"]), "the shuttle's route passes over crossings"
    drives = {"python": {"along": 0.0, "speed": 0.0}, "native": {"along": 0.0, "speed": 0.0}}
    # A fault on the junctions of a few signalled crossings, as a world event sets one
    junctions = sorted({c.junction for c in python.walks.crossings if c.control == 0})[:4]
    busiest = sorted(network.roads, key=lambda r: (-len(python.walks.by_road[r.index]), r.index))
    clock, held_by_walkers = 0.0, 0
    for i in range(3000):
        clock = (i + 1) * DT
        if i == 200:
            for traffic in (python, fast):
                for junction in junctions:
                    traffic.set_signal_fault(junction, clock + 60.0)
                closed = busiest[5]
                traffic.set_hazards([([(closed.xs[0], closed.ys[0]), (closed.xs[-1], closed.ys[-1])], False)])
                assert traffic.pin_vehicle("stall", busiest[8], busiest[8].length / 2, 0, clock + 90.0, 1)
        if i % 2 == 0 and drives["python"]["along"] < routes["python"].length - 2.0:
            plans = {name: _drive_shuttle(traffic, routes[name], drives[name], 2 * DT)
                     for name, traffic in (("python", python), ("native", fast))}
            assert plans["native"] == plans["python"], i
            held_by_walkers += plans["python"][4] < 10.0
        python.step(DT, clock)
        fast.step(DT, clock)
        if i % 100 == 99:
            assert fast.state() == python.state(), i
            assert fast.pedestrian_frame() == python.pedestrian_frame(), i
            assert fast.frame() == python.frame(), i
    assert fast.report() == python.report()
    assert python.report()["pedestrian_hits"] == []
    assert json.dumps(fast.state()) == json.dumps(python.state())
    assert held_by_walkers > 0, "the walkers held the shuttle up at a crossing on its way"


@native
@pytest.mark.traffic
def test_trips_the_time_of_day_and_a_jaywalker_play_out_identically_on_both_engines():
    """The phase 5 bar: walkers on trips, going in and coming out with the evening's curve, crossings let go
    lane by lane, and a jaywalker in front of a shuttle, bit for bit on both engines over 2,000 steps, with a
    checkpoint taken in the middle of the jaywalk carrying on identically in either."""
    evening = 17 * 3600.0
    python = CityTraffic.start("toa-payoh", pedestrians=True, day_start_s=evening)
    fast = NativeCityTraffic.start("toa-payoh", pedestrians=True, day_start_s=evening)
    assert fast.state() == python.state() and python.state()["day_start_s"] == evening
    points = _shuttle_route(python)
    routes = {"python": ShuttleRoute(points), "native": ShuttleRoute(points)}
    drives = {"python": {"along": 0.0, "speed": 0.0}, "native": {"along": 0.0, "speed": 0.0}}
    # The jaywalk happens on a road of the shuttle's route ahead of it, where a walker is near enough
    network = python.network
    ahead = []
    for (ax, ay), (bx, by) in zip(points[20:], points[21:]):
        road, _ = network.road_under((ax + bx) / 2, (ay + by) / 2, bx - ax, by - ay, 0.7)
        if road is not None and road not in ahead:
            ahead.append(road)
    jaywalked, checkpoint, reports = False, None, []
    for i in range(2000):
        clock = (i + 1) * DT
        if 100 <= i and not jaywalked:
            for road in ahead:
                sent = python.jaywalk("jay", road, road.length / 2, clock + 120.0)
                assert fast.jaywalk("jay", road, road.length / 2, clock + 120.0) == sent
                if sent:
                    jaywalked = True
                    break
        if i % 2 == 0 and drives["python"]["along"] < routes["python"].length - 2.0:
            plans = {name: _drive_shuttle(traffic, routes[name], drives[name], 2 * DT)
                     for name, traffic in (("python", python), ("native", fast))}
            assert plans["native"] == plans["python"], i
        python.step(DT, clock)
        fast.step(DT, clock)
        if checkpoint is None and python.jaywalks and python.jaywalks["jay"].phase >= 2:
            checkpoint = (i, json.loads(json.dumps(fast.state())))
        if i % 100 == 99:
            assert fast.state() == python.state(), i
            assert fast.pedestrian_frame() == python.pedestrian_frame(), i
            reports.append(python.report())
    assert jaywalked and checkpoint is not None, "a walker went across a road of the route"
    assert fast.report() == python.report() and python.report()["pedestrian_hits"] == []
    assert any(report["walkers_away"] for report in reports) and any(report["walkers_jaywalking"] for report in reports)
    # From the checkpoint taken while the jaywalker was on the road, both engines drive on as the run did
    at, state = checkpoint
    again = {CityTraffic: CityTraffic.from_state(state), NativeCityTraffic: NativeCityTraffic.from_state(state)}
    assert again[CityTraffic].state() == state == again[NativeCityTraffic].state()
    run(again[CityTraffic], 200, (at + 1) * DT)
    run(again[NativeCityTraffic], 200, (at + 1) * DT)
    assert again[CityTraffic].state() == again[NativeCityTraffic].state()


@native
@pytest.mark.parametrize("written_by", [CityTraffic, NativeCityTraffic])
def test_a_checkpoint_from_either_engine_drives_on_identically_in_the_other(written_by):
    traffic = written_by.start("toa-payoh")
    run(traffic, 100)
    state = json.loads(json.dumps(traffic.state()))
    python, fast = CityTraffic.from_state(state), NativeCityTraffic.from_state(state)
    assert json.dumps(fast.state()) == json.dumps(state) == json.dumps(python.state())
    run(python, 100, 10.0)
    run(fast, 100, 10.0)
    assert fast.state() == python.state()


@native
def test_a_native_checkpoint_is_refused_where_the_python_one_would_be():
    traffic = NativeCityTraffic.start("toa-payoh")
    state = traffic.state()
    for damaged in ({**state, "vehicles": state["vehicles"][:-1]},
                    {**state, "pins": [["x", [len(state["vehicles"])], 10.0]]},
                    {**state, "vehicles": [[10**6, *state["vehicles"][0][1:]], *state["vehicles"][1:]]}):
        with pytest.raises((ValueError, IndexError)):
            CityTraffic.from_state(damaged)
        with pytest.raises((ValueError, IndexError)):
            NativeCityTraffic.from_state(damaged)


@native
def test_a_staged_native_copy_never_changes_its_original():
    traffic = NativeCityTraffic.start("toa-payoh")
    run(traffic, 20)
    before = traffic.state()
    copy = deepcopy(traffic)
    run(copy, 20, 2.0)
    copy.set_hazards([([(0.0, 0.0), (500.0, 0.0)], True)])
    assert traffic.state() == before
    run(traffic, 20, 2.0)
    again = deepcopy(traffic)
    run(again, 5, 4.0)
    run(traffic, 5, 4.0)
    assert traffic.state() == again.state()


# ---------------------------------------------------------------- whole sessions

def _session(engine: str, seed: int, monkeypatch) -> IntegratedSessionActor:
    monkeypatch.setenv(native_traffic.ENGINE_VARIABLE, engine)
    actor = IntegratedSessionActor(seed, session_id=f"engines-{seed}")
    actor.is_paused = False
    actor.modes = {s.ID: "IN_SERVICE" for s in actor.shuttles}
    return actor


@native
@pytest.mark.traffic
def test_a_session_staged_tick_by_tick_is_identical_on_both_engines(monkeypatch):
    python, fast = _session("0", 1000, monkeypatch), _session("1", 1000, monkeypatch)
    assert type(python.traffic) is CityTraffic and type(fast.traffic) is NativeCityTraffic
    # Long enough for its first random incident (a road accident pinning two vehicles near tick 2130)
    for tick in range(2400):
        python, fast = deepcopy(python), deepcopy(fast)
        python.tick()
        fast.tick()
        if tick % 200 == 0 or tick > 2100 and tick % 20 == 0:
            assert without_event_ids(fast.workflow_state()) == without_event_ids(python.workflow_state()), tick
            assert fast.traffic_frame() == python.traffic_frame(), tick
    assert python.traffic.pins, "the session's road accident was played out in the traffic"
    snapshot = lambda actor: without_event_ids(actor.generate_snapshot().model_dump(mode="json"))
    assert snapshot(fast) == snapshot(python)


@native
def test_sessions_step_their_traffic_on_several_threads_at_once_exactly_as_one_by_one():
    """The native engine lets go of the GIL while it steps: sessions on several threads must still each
    drive exactly as they would alone."""
    start = NativeCityTraffic.start("toa-payoh")
    run(start, 10)
    alone = [deepcopy(start) for _ in range(4)]
    together = [deepcopy(start) for _ in range(4)]
    for index, traffic in enumerate(alone + together):
        traffic.set_hazards([([(index * 100.0, 0.0), (index * 100.0 + 300.0, 0.0)], True)])
    for traffic in alone:
        run(traffic, 150, 1.0)
    errors = []

    def drive(traffic):
        try:
            run(traffic, 150, 1.0)
        except Exception as error:  # Reported below
            errors.append(error)

    threads = [threading.Thread(target=drive, args=(traffic,)) for traffic in together]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    assert not errors
    assert [t.state() for t in together] == [t.state() for t in alone]


# ---------------------------------------------------------------- choosing the engine

def test_the_engine_follows_the_setting(monkeypatch):
    monkeypatch.setenv(native_traffic.ENGINE_VARIABLE, "0")
    assert native_traffic.engine() is CityTraffic
    monkeypatch.setenv(native_traffic.ENGINE_VARIABLE, "auto")
    assert native_traffic.engine() is (NativeCityTraffic if native_traffic.status()[0] else CityTraffic)
    monkeypatch.setenv(native_traffic.ENGINE_VARIABLE, "sometimes")
    with pytest.raises(ValueError):
        native_traffic.engine()


def test_requiring_the_native_engine_where_it_cannot_run_says_why(monkeypatch):
    monkeypatch.setattr(native_traffic, "_status", (False, "the native module is not built"))
    monkeypatch.setenv(native_traffic.ENGINE_VARIABLE, "auto")
    assert native_traffic.engine() is CityTraffic
    monkeypatch.setenv(native_traffic.ENGINE_VARIABLE, "1")
    with pytest.raises(RuntimeError, match="not built"):
        native_traffic.engine()


@native
def test_a_module_built_from_older_source_is_not_used(tmp_path, monkeypatch):
    changed = tmp_path / "traffic_core.cpp"
    changed.write_bytes(native_traffic.SOURCE.read_bytes() + b"\n// changed\n")
    monkeypatch.setattr(native_traffic, "SOURCE", changed)
    monkeypatch.setattr(native_traffic, "_status", None)
    usable, reason = native_traffic.status()
    assert not usable and "older" in reason
    monkeypatch.setenv(native_traffic.ENGINE_VARIABLE, "auto")
    assert native_traffic.engine() is CityTraffic


@native
def test_a_network_with_unnumbered_junctions_is_driven_in_python():
    from src.traffic.road_network import RoadNetwork
    roads = [{"id": "a", "class": "primary", "from_node": "x", "to_node": "y", "oneway": True, "lanes": 1,
              "speed_kph": 50, "points_m": [(0.0, 0.0), (100.0, 0.0)]}]
    network = RoadNetwork("test", {"models": {}, "vehicles": [], "roads": roads}, ())
    assert type(NativeCityTraffic.on(network)) is CityTraffic
