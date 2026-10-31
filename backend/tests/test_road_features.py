import json

import pytest
from fastapi.testclient import TestClient

from src.app import app
from src.routing.build_singapore_fleet_fixture import SERVICE_AREAS, project
from src.traffic import signal_cycle
from src.traffic.import_road_features import (
    build,
    approach_for_head,
    approaches_of,
    build_crossings,
    build_signals,
    group_junctions,
    heading_of_bearing,
    output_path,
    phases_of,
    zebra_stripes,
)

BOX = SERVICE_AREAS["toa-payoh"]["box"]
LAT, LON = 1.335, 103.85
METRE = 1 / 111_000


def head(north_m=0.0, east_m=0.0, bearing=0.0, code="G"):
    return {"points": [(LAT + north_m * METRE, LON + east_m * METRE)], "type_code": code, "road_code": "TOP01A",
            "word": "", "bearing_deg": bearing, "level": 2}


def line(*points_m):
    return {"points": [(LAT + north * METRE, LON + east * METRE) for north, east in points_m], "type_code": "", "road_code": "",
            "word": "", "bearing_deg": None, "level": None}


def test_a_four_way_junction_alternates_its_two_roads():
    phases = phases_of([0, 180, 90, 270])
    assert phases[0] == phases[1] and phases[2] == phases[3] and phases[0] != phases[2]


def test_opposite_heads_share_a_phase_on_a_road_at_an_angle():
    phases = phases_of([33, 213, 125, 305, 36])
    assert phases[0] == phases[1] == phases[4] and phases[2] == phases[3] and phases[0] != phases[2]


def test_a_road_across_north_stays_one_road():
    assert len(set(phases_of([178, 2, 358, 182]))) == 1


def test_a_signal_on_one_road_is_all_the_first_phase():
    assert phases_of([40, 220]) == [0, 0] and phases_of([70]) == [0]


def test_the_busier_road_is_the_first_phase():
    assert phases_of([0, 180, 0, 90]) == [0, 0, 0, 1]


def test_a_large_junction_with_spread_out_approaches_still_takes_turns():
    phases = phases_of([210, 230, 25, 345, 20, 230, 355, 77.7, 355, 85, 250])
    assert len(set(phases)) == 2 and phases[3] == phases[6] == phases[8] and phases[0] == phases[1] == phases[7] != phases[3]


def test_near_heads_are_one_junction_and_far_ones_are_not():
    groups = group_junctions([(0, 0), (50, 0), (100, 0), (500, 0)])
    assert sorted(map(sorted, groups)) == [[0, 1, 2], [3]]


def test_signals_outside_the_area_are_left_out():
    rows = [head(), head(east_m=10, bearing=90), {**head(), "points": [(BOX[2] + 0.0003, LON)]}]
    data = build_signals(rows, BOX, [])
    assert len(data["signals"]) == 2 and data["junctions"][0]["head_count"] == 2
    assert {signal["phase"] for signal in data["signals"]} == {0, 1}


def road(identifier, points, oneway=True):
    return {"id": identifier, "name": identifier, "class": "residential", "from_node": 1, "to_node": 2, "oneway": oneway,
            "lanes": 2, "speed_kph": 40, "points_m": points}


NORTHBOUND = road("north", [[0.0, -100.0], [0.0, 0.0]])
EASTBOUND = road("east", [[-100.0, 0.0], [0.0, 0.0]])


def test_roads_ending_at_the_heads_lead_in_and_ones_leaving_do_not():
    leaving = road("away", [[0.0, 0.0], [100.0, 0.0]])
    inside = road("link", [[0.0, 0.0], [5.0, 5.0]])
    approaches = approaches_of([(3.0, -5.0)], [NORTHBOUND, EASTBOUND, leaving, inside])
    assert sorted((a["road"], a["heading_deg"]) for a in approaches) == [("east", 0.0), ("north", 90.0)]
    north = next(a for a in approaches if a["road"] == "north")
    assert (north["stop_x_m"], north["stop_y_m"]) == (0.0, -8.0)


def test_a_two_way_road_leads_in_from_its_far_end_only():
    both = approaches_of([(0.0, 0.0)], [road("both", [[0.0, -100.0], [0.0, 0.0]], oneway=False)])
    assert [a["heading_deg"] for a in both] == [90.0]


def test_a_signal_between_junctions_uses_the_road_running_past_it():
    through = road("through", [[0.0, -200.0], [0.0, 200.0]], oneway=False)
    approaches = approaches_of([(2.0, 0.0), (-2.0, 0.0)], [through])
    assert sorted(a["heading_deg"] for a in approaches) == [90.0, 270.0]
    assert all(a["stop_y_m"] == (-8.0 if a["heading_deg"] == 90.0 else 8.0) for a in approaches)


def test_a_head_controls_the_direction_it_is_on_the_kerb_side_of():
    through = road("through", [[0.0, -200.0], [0.0, 200.0]], oneway=False)
    approaches = approaches_of([(2.0, 0.0), (-2.0, 0.0)], [through])
    # Singapore drives on the left, so a northbound driver's kerb is on the west side
    assert approach_for_head((-4.0, -9.0), approaches)["heading_deg"] == 90.0
    assert approach_for_head((4.0, 9.0), approaches)["heading_deg"] == 270.0


def test_a_head_far_from_every_approach_controls_none():
    assert approach_for_head((0.0, 500.0), approaches_of([(0.0, 0.0)], [NORTHBOUND])) is None


def test_heads_face_the_traffic_they_control_and_take_its_phase():
    south_of_it = head(north_m=-15, east_m=-3, bearing=7)
    west_of_it = head(north_m=3, east_m=-15, bearing=200)
    origin = (project(LAT, LON)[0], project(LAT, LON)[1])
    shifted = [{**r, "points_m": [[x + origin[0], y + origin[1]] for x, y in r["points_m"]]} for r in (NORTHBOUND, EASTBOUND)]
    data = build_signals([south_of_it, west_of_it], BOX, shifted)
    by_direction = {signal["traffic_heading_deg"]: signal for signal in data["signals"]}
    assert set(by_direction) == {0.0, 90.0}
    assert by_direction[90.0]["facing_heading_deg"] == 270.0 and by_direction[0.0]["facing_heading_deg"] == 180.0
    assert by_direction[90.0]["phase"] != by_direction[0.0]["phase"]
    assert all(signal["direction_source"] == "road_map" for signal in data["signals"])
    assert len(data["junctions"][0]["approaches"]) == 2


def test_a_head_off_the_road_map_falls_back_on_ltas_bearing():
    signal = build_signals([head(bearing=0)], BOX, [])["signals"][0]
    assert signal["direction_source"] == "lta_bearing" and signal["approach"] is None
    assert signal["traffic_heading_deg"] == 90.0 and signal["facing_heading_deg"] == 270.0


def test_bearings_turn_into_headings():
    assert [heading_of_bearing(bearing) for bearing in (0, 90, 180, 270)] == [90, 0, 270, 180]


def test_zebra_bars_step_across_the_road_and_run_along_it():
    stripes = zebra_stripes([[0.0, 0.0], [0.0, 6.0]])  # Walking north across an east-west road
    assert len(stripes) == 6
    assert stripes[0] == [[1.5, 0.25], [1.5, 0.75], [-1.5, 0.75], [-1.5, 0.25]]
    # Bars keep their thickness and gap, and stay within the walking line
    assert [bar[0][1] for bar in stripes] == [0.25, 1.25, 2.25, 3.25, 4.25, 5.25]
    assert stripes[-1][1][1] <= 6


def test_a_crossing_gets_edges_with_a_signal_and_stripes_without():
    data = build_crossings([line((0, 0), (0, 12)), line((500, 0), (500, 12))], [head(north_m=2, east_m=6)])
    near, far = data["crossings"]
    assert len(near["edges_m"]) == 2 and "stripes_m" not in near
    assert len(far["stripes_m"]) == 12 and "edges_m" not in far
    assert data["crossing_shape"]["assumed"] is True


def test_a_crossing_is_signalised_only_with_a_head_close_by():
    rows = [line((0, 0), (0, 12)), line((500, 0), (500, 12))]
    crossings = build_crossings(rows, [head(north_m=2, east_m=6)])["crossings"]
    near, far = crossings
    assert near["control"] == "signal" and near["render_as"] == "signal_crossing" and near["signal_distance_m"] == pytest.approx(2, abs=0.1)
    assert far["control"] == "no_signal" and far["render_as"] == "zebra" and far["signal_distance_m"] > 400


def test_a_crossing_with_no_heads_at_all_is_not_signalised():
    assert build_crossings([line((0, 0), (0, 12))], [])["crossings"][0]["signal_distance_m"] is None


def test_each_phase_runs_green_yellow_red_in_order():
    def color(t):
        return signal_cycle.phase_state(t, 0, 0)["color"]

    assert [color(t) for t in (0, 29.9, 30, 32.9, 33, 69.9, 70)] == ["green", "green", "yellow", "yellow", "red", "red", "green"]
    assert signal_cycle.phase_state(10, 0, 0)["seconds_left"] == 20
    assert signal_cycle.phase_state(31, 0, 0)["seconds_left"] == 2
    assert signal_cycle.phase_state(40, 0, 0)["seconds_left"] == 30


@pytest.mark.parametrize("offset", [0, 13, 69])
def test_two_phases_are_never_both_allowed_to_go(offset):
    for tenth in range(signal_cycle.CYCLE_S * 10):
        colors = [signal_cycle.phase_state(tenth / 10, offset, phase)["color"] for phase in range(signal_cycle.PHASES)]
        assert colors.count("red") >= 1
    # Every colour turns up for each phase
    seen = {signal_cycle.phase_state(t, offset, 1)["color"] for t in range(signal_cycle.CYCLE_S)}
    assert seen == {"green", "yellow", "red"}


def test_the_same_inputs_build_the_same_files():
    layers = {"RoadCrossing": [line((0, 0), (0, 12))], "LaneMarking": [line((0, 0), (9, 0))], "ArrowMarking": [head(bearing=9)],
              "WordMarking": [{**head(), "word": "SLOW"}], "RoadHump": [line((1, 0), (1, 5))]}
    first = build(layers, [head()], [], "toa-payoh", "Sep2026", "2026-10-31")
    second = build({key: value[::-1] for key, value in layers.items()}, [head()], [], "toa-payoh", "Sep2026", "2026-10-31")
    assert first == second
    assert first["signals"]["timing"]["simulated"] is True
    assert first["markings"]["words"][0]["text"] == "SLOW"


def strict_json(path):
    def refuse(value):
        raise ValueError(value)

    return json.loads(path.read_text(encoding="utf-8"), parse_constant=refuse)


def test_the_committed_files_are_complete_and_inside_the_area():
    signals = strict_json(output_path("toa-payoh", "signals"))
    crossings = strict_json(output_path("toa-payoh", "crossings"))
    markings = strict_json(output_path("toa-payoh", "markings"))
    assert len(signals["signals"]) > 500 and len(signals["junctions"]) > 40
    assert len(crossings["crossings"]) > 100
    assert {"signal_crossing", "zebra"} == {crossing["render_as"] for crossing in crossings["crossings"]}
    assert len(markings["lane_lines"]) > 1000 and markings["arrows"] and markings["words"] and markings["road_humps"]

    known = {junction["id"] for junction in signals["junctions"]}
    west, south, east, north = signals["area"]["box_m"]
    approaches = {approach["id"]: approach for junction in signals["junctions"] for approach in junction["approaches"]}
    for signal in signals["signals"]:
        assert signal["junction"] in known and signal["phase"] in (0, 1)
        assert abs((signal["facing_heading_deg"] - signal["traffic_heading_deg"]) % 360 - 180) < 0.11
        if signal["approach"] is not None:
            assert approaches[signal["approach"]]["phase"] == signal["phase"]
        assert west <= signal["x_m"] <= east and south <= signal["y_m"] <= north
    for data in (signals, crossings, markings):
        assert "Singapore Open Data Licence" in data["attribution"][0]


def test_the_committed_files_hold_no_staff_details():
    for kind in ("signals", "crossings", "markings"):
        text = output_path("toa-payoh", kind).read_text(encoding="utf-8")
        for column in ("CRT_USRID", "LAST_UPD", "JOB_NUM", "DASAID", "CORIASQD"):
            assert column not in text


def test_the_api_serves_the_imported_files_and_the_colours():
    client = TestClient(app)
    signals = client.get("/api/road-features/toa-payoh/signals").json()
    assert signals["timing"]["simulated"] is True and signals["signals"]
    assert client.get("/api/road-features/toa-payoh/crossings").json()["crossings"]
    assert client.get("/api/road-features/toa-payoh/markings").json()["lane_lines"]

    state = client.get("/api/road-features/toa-payoh/signals/state", params={"t": 1000}).json()
    assert state["simulated"] is True and state["t"] == 1000
    assert len(state["junctions"]) == len(signals["junctions"])
    first = signals["junctions"][0]
    assert state["junctions"][0]["phases"][0] == signal_cycle.phase_state(1000, first["offset_s"], 0)
    assert client.get("/api/road-features/toa-payoh/signals/state").json()["t"] > 1_000_000


@pytest.mark.parametrize("path", ["nowhere/signals", "nowhere/signals/state", "nowhere/crossings", "nowhere/markings", "..%2F..%2Fsecret/signals"])
def test_an_area_with_no_data_is_not_found(path):
    assert TestClient(app).get(f"/api/road-features/{path}").status_code == 404
