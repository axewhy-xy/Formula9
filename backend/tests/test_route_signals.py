"""
Tests for shuttles stopping at traffic lights (backend/src/traffic/route_signals.py and its use in
backend/src/services/integrated_actor.py).

Description:
    Checks which stop lines a route crosses (only those facing its way, close to it), when a light
    holds a shuttle (red, or a yellow it can stop for) and that in the live session no shuttle's
    front crosses a stop line while its light is red, though the lights do hold shuttles up.
"""

from src.services.integrated_actor import SHUTTLE_FRONT_M, IntegratedSessionActor
from src.traffic import route_signals, signal_cycle

NORTH = [(0.0, 0.0), (0.0, 300.0)]


def junction(heading_deg, stop=(1.0, 100.0), offset_s=0, phase=0):
    return {"id": "j", "offset_s": offset_s,
            "approaches": [{"heading_deg": heading_deg, "stop_x_m": stop[0], "stop_y_m": stop[1], "phase": phase}]}


def test_a_route_stops_at_a_line_facing_its_way():
    lines = route_signals.stop_lines(NORTH, [junction(90)])
    assert [round(line.progress_m, 3) for line in lines] == [100.0]


def test_a_route_ignores_a_line_facing_the_other_way_or_on_another_road():
    assert route_signals.stop_lines(NORTH, [junction(270)]) == []
    assert route_signals.stop_lines(NORTH, [junction(0)]) == [], "on the road crossing it"
    assert route_signals.stop_lines(NORTH, [junction(90, stop=(30.0, 100.0))]) == [], "on a road alongside"


def test_a_junctions_lanes_are_one_line():
    both = {"id": "j", "offset_s": 0, "approaches": [
        {"heading_deg": 90, "stop_x_m": 1.0, "stop_y_m": 100.0, "phase": 0},
        {"heading_deg": 90, "stop_x_m": 4.0, "stop_y_m": 101.0, "phase": 0}]}
    assert len(route_signals.stop_lines(NORTH, [both])) == 1


def test_a_red_holds_a_shuttle_and_a_green_does_not():
    line = route_signals.StopLine(100.0, offset_s=0, phase=0)
    assert not route_signals.must_stop(line, 0.0, 50.0, 10.0), "green"
    assert route_signals.must_stop(line, signal_cycle.GREEN_S + signal_cycle.YELLOW_S + 1, 50.0, 10.0), "red"
    assert not route_signals.must_stop(line, signal_cycle.GREEN_S + signal_cycle.YELLOW_S + 1, -1.0, 10.0), "already over the line"


def test_a_yellow_holds_only_a_shuttle_that_can_stop_for_it():
    line = route_signals.StopLine(100.0, offset_s=0, phase=0)
    yellow = signal_cycle.GREEN_S + 1
    assert route_signals.must_stop(line, yellow, 20.0, 10.0), "10 m/s stops in 8.3 m"
    assert not route_signals.must_stop(line, yellow, 5.0, 10.0), "too close to stop: on through it"


def test_the_hold_point_is_the_shuttles_front_short_of_the_line():
    line = route_signals.StopLine(100.0, offset_s=0, phase=0)
    red = signal_cycle.GREEN_S + signal_cycle.YELLOW_S + 1
    assert route_signals.hold_point([line], red, 50.0, 6.6, 10.0) == 100.0 - 6.6 - route_signals.STOP_MARGIN_M
    assert route_signals.hold_point([line], 0.0, 50.0, 6.6, 10.0) is None
    assert route_signals.hold_point([line], red, 93.0, 6.6, 0.0) == 93.0, "never back"


def test_the_signal_file_is_read():
    assert len(route_signals.junctions("toa-payoh")) > 10
    assert route_signals.junctions("nowhere") == ()


def test_no_shuttle_runs_a_red_light_and_the_lights_hold_them_up():
    actor = IntegratedSessionActor(0, session_id="signal-test")
    actor.is_paused = False
    actor.modes = {s.ID: "IN_SERVICE" for s in actor.shuttles}
    junctions = route_signals.junctions("toa-payoh")
    held = 0
    known = {}
    for _ in range(6000):  # Five minutes
        before = {s.ID: (s.route_ID, s.progress) for s in actor.shuttles}
        actor.tick()
        for shuttle in actor.shuttles:
            route_id, progress = before[shuttle.ID]
            if shuttle.route_ID != route_id or shuttle.progress < progress:
                continue  # A new route or a new lap
            waypoints = actor._routes[route_id]["waypoints"]
            if known.get(route_id, (None,))[0] is not waypoints:
                known[route_id] = (waypoints, route_signals.stop_lines([(p.x, p.y) for p in waypoints], junctions))
            lines = known[route_id][1]
            for line in lines:
                color = signal_cycle.phase_state(actor.timestamp_second, line.offset_s, line.phase)["color"]
                crossed = progress + SHUTTLE_FRONT_M <= line.progress_m < shuttle.progress + SHUTTLE_FRONT_M
                assert not (crossed and color == "red"), f"shuttle {shuttle.ID} ran the red light at {line.progress_m:.0f} m"
                waiting = abs(line.progress_m - SHUTTLE_FRONT_M - route_signals.STOP_MARGIN_M - shuttle.progress) < 1e-6
                if waiting and shuttle.progress == progress and actor.modes[shuttle.ID] == "IN_SERVICE":
                    held += 1
    assert held > 0, "a red light held a shuttle at its line"
