import json
import math
import re
from pathlib import Path

import pytest
import shapely
from fastapi.testclient import TestClient
from shapely.geometry import LineString, Polygon

from src.app import app
from src.traffic import import_walkways as walkways
from src.traffic.import_road_features import output_path

REPOSITORY = Path(__file__).resolve().parents[2]
WORLD = REPOSITORY / "Assets" / "Formula9" / "Runtime" / "World"
BUNDLED = REPOSITORY / "Assets" / "Formula9" / "Resources" / "Formula9" / "RoadData" / "toa-payoh-walkways.json"


def road(road_id="way/1/1", points=((0, 0), (100, 0)), road_class="residential", oneway=False, lanes=1):
    return {"id": road_id, "class": road_class, "oneway": oneway, "lanes": lanes, "points_m": [list(p) for p in points]}


def triangles(data):
    """Every paved triangle of a built file, as (corners in metres, zone)."""
    found = []
    for tile in data["render"]["tiles"]:
        v = tile["vertices_cm"]
        corner = lambda i: (v[2 * i] / 100, v[2 * i + 1] / 100)  # noqa: E731
        for zone, indexes in tile["zones"].items():
            found += [((corner(indexes[i]), corner(indexes[i + 1]), corner(indexes[i + 2])), zone) for i in range(0, len(indexes), 3)]
        ramps = tile["ramp_triangles"]
        found += [((corner(ramps[i]), corner(ramps[i + 1]), corner(ramps[i + 2])), "ramp") for i in range(0, len(ramps), 4)]
    return found


def paved(data):
    return shapely.union_all([Polygon(corners) for corners, _ in triangles(data)])


def asphalt(roads):
    return shapely.union_all([part for item in roads for part in walkways.carriageway(walkways.line_of(item), walkways.half_width(item))])


# A crossroads of two two-way streets, a crossing over the eastern arm, and a building on one corner
CROSSROADS = [road("way/1/1", ((-80, 0), (0, 0))), road("way/1/2", ((0, 0), (80, 0))),
              road("way/2/1", ((0, -80), (0, 0)), "primary"), road("way/2/2", ((0, 0), (0, 80)), "primary")]
CROSSING = [{"id": "crossing-001", "line_m": [[30, -4.0], [30, 4.0]]}]
BUILDING = [{"id": "way/9", "rings_m": [[[6, 6], [40, 6], [40, 40], [6, 40], [6, 6]]]}]


def test_osm_sides_are_of_the_way_as_drawn_so_a_reversed_one_way_swaps_them():
    one_way = road(oneway=True)
    assert walkways.sidewalk_sides(one_way, {"sidewalk": "left"}) == (True, False)
    # The prepared road runs with the traffic, against the way as drawn: its left is the way's right
    assert walkways.sidewalk_sides(one_way, {"sidewalk": "left", "oneway": "-1"}) == (False, True)
    assert walkways.sidewalk_sides(one_way, {"sidewalk:right": "yes", "oneway": "-1"}) == (True, False)
    data = walkways.build([one_way], {1: {"sidewalk": "left", "oneway": "-1"}}, [], [], "test")
    assert paved(data).centroid.y < 0, "on the right of the traffic"


def test_untagged_roads_take_their_class_default_and_tags_override_it():
    assert walkways.sidewalk_sides(road(), {}) == (True, True)
    assert walkways.sidewalk_sides(road(oneway=True), {}) == (True, False), "a one-way road's right is a central divider"
    assert walkways.sidewalk_sides(road(road_class="service"), {}) == (False, False)
    assert walkways.sidewalk_sides(road(road_class="primary_link"), {}) == (False, False)
    assert walkways.sidewalk_sides(road(), {"sidewalk": "no"}) == (False, False)
    assert walkways.sidewalk_sides(road(road_class="service"), {"sidewalk": "both"}) == (True, True)
    assert walkways.sidewalk_sides(road(), {"sidewalk": "separate"}) == (True, True), "a separate footpath counts for now"
    assert walkways.sidewalk_sides(road(), {"sidewalk:both": "no", "sidewalk:left": "yes"}) == (True, False)


def test_a_sidewalk_runs_beside_its_road_and_never_on_it():
    data = walkways.build([road()], {}, [], [], "test")
    area = paved(data)
    reach = walkways.half_width(road()) + walkways.KERB_STONE_M + 0.5 + 1.8
    # Both sides, from the kerb out past verge and footpath, and rounded off round the ends
    middle = area.intersection(Polygon([(10, -20), (90, -20), (90, 20), (10, 20)]))
    assert middle.area == pytest.approx(2 * 80 * (reach - walkways.half_width(road())), rel=0.01)
    assert middle.bounds[1] == pytest.approx(-reach, abs=0.02) and middle.bounds[3] == pytest.approx(reach, abs=0.02)
    assert area.bounds[0] < -walkways.half_width(road()), "round the dead end"
    assert area.intersection(asphalt([road()])).area < 0.02
    assert {zone for _, zone in triangles(data)} == {"footpath", "kerb_stone", "verge"}
    assert data["report"]["ramps"] == 0


def test_corners_join_and_crossing_asphalt_cuts_every_sidewalk():
    data = walkways.build(CROSSROADS, {}, CROSSING, BUILDING, "test")
    area = paved(data)
    for overlap in shapely.get_parts(area.intersection(asphalt(CROSSROADS))):
        assert overlap.area <= 0.02
    near = area.intersection(Polygon([(-40, -40), (40, -40), (40, 40), (-40, 40)]))
    assert len([part for part in shapely.get_parts(near) if part.area > 1]) == 4, "one paved corner between each pair of arms, cut by the asphalt between them"
    assert area.intersection(Polygon([(6, 6), (40, 6), (40, 40), (6, 40)])).area < 0.02, "not inside the building"
    assert area.intersection(Polygon([(4, 4), (6, 4), (6, 6), (4, 6)])).area > 1, "but up to its wall"


def test_a_crossing_drops_the_kerb_to_the_road_at_each_end():
    data = walkways.build(CROSSROADS, {}, CROSSING, BUILDING, "test")
    report = data["report"]
    assert report["ramps"] == 2 and report["crossings_with_both_ramps"] == 1 and not report["unramped_crossing_ends"]
    # The western arm's side-street crossing has ramps of its own; these are crossing-001's, 30 m east
    ramps = [corners for corners, zone in triangles(data) if zone == "ramp" and Polygon(corners).centroid.x > 20]
    ramp_area = sum(Polygon(corners).area for corners in ramps)
    assert ramp_area == pytest.approx(2 * walkways.RAMP_WIDTH_M * walkways.RAMP_DEPTH_M, rel=0.02)
    kerb = walkways.half_width(CROSSROADS[0])
    tile_ramps = [ramp for tile in data["render"]["tiles"] for ramp in tile["ramps"] if ramp[0] > 2000]
    found = sorted((ramp[0] / 100, ramp[1] / 100, ramp[2] / 10000, ramp[3] / 10000) for ramp in tile_ramps)
    assert found == [pytest.approx((30, -kerb, 0, -1)), pytest.approx((30, kerb, 0, 1))], "at each kerb, facing away from the road"
    assert sum(len(tile["cheek_edges"]) for tile in data["render"]["tiles"]) > 0, "ramps have sides"


def test_a_crossing_to_a_kerb_without_a_sidewalk_gets_somewhere_to_land():
    one_way = road(oneway=True, lanes=2)
    data = walkways.build([one_way], {}, [{"id": "crossing-001", "line_m": [[50, -5], [50, 5]]}], [], "test")
    assert data["report"]["landings"] == 1 and data["report"]["ramps"] == 2


def test_triangles_face_up_and_edges_point_at_their_tile_corners():
    data = walkways.build(CROSSROADS, {}, CROSSING, BUILDING, "test")
    for (a, b, c), _ in triangles(data):
        assert (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]) < 0, "clockwise seen from above"
    for tile in data["render"]["tiles"]:
        corners = len(tile["vertices_cm"]) // 2
        for kind in ("kerb_edges", "outer_edges", "cheek_edges"):
            edges = tile[kind]
            assert len(edges) % 3 == 0
            assert all(0 <= edges[i] < corners and 0 <= edges[i + 1] < corners and -1 <= edges[i + 2] < len(tile["ramps"])
                       for i in range(0, len(edges), 3))
        assert tile["kerb_edges"] and tile["outer_edges"]


def test_the_same_inputs_build_the_same_file_in_any_order():
    first = walkways.build(CROSSROADS, {}, CROSSING, BUILDING, "test")
    second = walkways.build(CROSSROADS[::-1], {}, CROSSING, BUILDING, "test")
    assert json.dumps(first, sort_keys=True) == json.dumps(second, sort_keys=True)


def csharp_constant(path: Path, name: str) -> float:
    match = re.search(rf"\b{name}\s*=\s*([0-9.]+)f?", path.read_text(encoding="utf-8"))
    assert match, f"{name} in {path.name}"
    return float(match.group(1))


def test_the_sizes_are_the_ones_the_3d_view_draws():
    road_mesh, sidewalk_mesh = WORLD / "RoadMesh.cs", WORLD / "SidewalkMesh.cs"
    assert csharp_constant(road_mesh, "LaneWidthM") == walkways.LANE_WIDTH_M
    assert csharp_constant(road_mesh, "ShoulderM") == walkways.SHOULDER_M
    assert csharp_constant(road_mesh, "SurfaceY") == walkways.SURFACE_Y
    assert csharp_constant(road_mesh, "SharpBendCosine") == walkways.SHARP_BEND_COSINE
    assert csharp_constant(road_mesh, "MaxMitre") == walkways.MAX_MITRE
    assert csharp_constant(road_mesh, "CapSides") == walkways.CAP_SIDES
    assert csharp_constant(sidewalk_mesh, "KerbRiseM") == walkways.KERB_RISE_M
    assert csharp_constant(sidewalk_mesh, "KerbStoneM") == walkways.KERB_STONE_M
    assert csharp_constant(sidewalk_mesh, "RampDepthM") == walkways.RAMP_DEPTH_M


@pytest.fixture(scope="module")
def committed():
    return json.loads(output_path("toa-payoh", "walkways").read_text(encoding="utf-8"))


def test_the_committed_file_is_rebuilt_byte_for_byte_from_its_inputs(tmp_path, committed):
    """A stale file (the roads, tags, crossings or buildings changed) or a run that varies fails here."""
    rebuilt = tmp_path / "walkways.json"
    walkways.write(rebuilt, walkways.build_area("toa-payoh"))
    assert rebuilt.read_bytes() == output_path("toa-payoh", "walkways").read_bytes(), "run python -m src.traffic.import_walkways"
    assert BUNDLED.read_bytes() == rebuilt.read_bytes(), "the Unity copy matches (--unity-copy)"


def test_the_committed_sidewalks_never_cover_the_asphalt(committed):
    """What touches the asphalt is the centimetre grid's rounding along kerbs: slivers under 2 cm wide.

    A sliver's area grows with the kerb's length (a triangle of kerb stone can run 100 m), so its
    width is what is checked: nothing is left of the overlap after shaving a centimetre off every side.
    """
    roads, _, _, _ = walkways.inputs("toa-payoh")
    area = paved(committed)
    overlap = area.intersection(asphalt(roads))
    assert overlap.buffer(-0.01).is_empty
    assert overlap.area < 0.001 * area.area


def test_the_committed_file_ramps_nearly_every_crossing_that_meets_a_kerb(committed):
    report = committed["report"]
    ends = 2 * report["crossings"]
    off_road, unramped = report["crossing_ends_off_roads"], report["unramped_crossing_ends"]
    assert report["ramps"] + len(unramped) + len(off_road) == ends, "every crossing end is accounted for"
    assert report["ramps"] >= 0.9 * (ends - len(off_road))
    assert report["triangles"] > 20_000 and committed["render"]["tiles"]


def test_the_api_serves_the_walkways_gzipped_to_a_client_that_takes_it():
    client = TestClient(app)
    packed = client.get("/api/road-features/toa-payoh/walkways", headers={"Accept-Encoding": "gzip"})
    assert packed.status_code == 200 and packed.headers["content-encoding"] == "gzip"
    assert packed.json()["area"] == "toa-payoh"
    plain = client.get("/api/road-features/toa-payoh/walkways", headers={"Accept-Encoding": "identity"})
    assert "content-encoding" not in plain.headers
    assert plain.content == output_path("toa-payoh", "walkways").read_bytes()


@pytest.mark.parametrize("path", ["nowhere/walkways", "..%2F..%2Fsecret/walkways"])
def test_an_area_with_no_walkways_is_not_found(path):
    assert TestClient(app).get(f"/api/road-features/{path}").status_code == 404


# --- The walk network (phase 2) ---------------------------------------------------------------------------

# A light at the crossroads: its east arm's westbound traffic (phase 1) is what crossing-001 cuts across; its
# northbound approach runs along the crossing, so it can never be the one
JUNCTIONS = [{"id": "J1", "approaches": [{"heading_deg": 180.0, "stop_x_m": 20.0, "stop_y_m": 2.0, "phase": 1},
                                         {"heading_deg": 90.0, "stop_x_m": 0.0, "stop_y_m": -10.0, "phase": 0}]}]
SIGNALLED = [{**CROSSING[0], "control": "signal"}]


def walk_lines(data, kind=None):
    return [LineString(way["points_m"]) for way in data["walkways"] if kind is None or way["kind"] == kind]


def parts_of(data):
    """Each node's connected part, by a walk over the walkways."""
    joined = {node["id"]: set() for node in data["nodes"]}
    for way in data["walkways"]:
        joined[way["from"]].add(way["to"])
        joined[way["to"]].add(way["from"])
    part, count = {}, 0
    for start in joined:
        if start in part:
            continue
        stack = [start]
        while stack:
            node = stack.pop()
            if node not in part:
                part[node] = count
                stack.extend(joined[node])
        count += 1
    return part


def test_walkways_run_down_the_footpaths_and_never_onto_the_road():
    data = walkways.build(CROSSROADS, {}, CROSSING, BUILDING, "test")
    area = paved(data).buffer(0.05)
    road_area = asphalt(CROSSROADS).buffer(-0.1)
    for line in walk_lines(data, "sidewalk"):
        assert area.covers(line), "on the paving drawn"
    for line in walk_lines(data, "link"):
        assert not line.intersects(road_area), "a link goes round a corner or an end, never over the road"
    footpath = walkways.half_width(CROSSROADS[0]) + walkways.KERB_STONE_M + 0.5 + 1.8 / 2
    north_side = [way for way in data["walkways"]
                  if way["kind"] == "sidewalk" and way["road_id"] == "way/1/2" and way["side"] == "left"]
    assert north_side and all(y == pytest.approx(footpath, abs=0.02) for way in north_side for _, y in way["points_m"]), \
        "down the middle of the footpath"
    assert {way["width_m"] for way in north_side} == {1.8} and {way["lanes"] for way in north_side} == {2}


def test_walkway_ends_are_their_nodes_and_no_two_nodes_are_closer_than_the_merge_distance():
    data = walkways.build(CROSSROADS, {}, CROSSING, BUILDING, "test")
    places = [tuple(node["xy_m"]) for node in data["nodes"]]
    assert [node["id"] for node in data["nodes"]] == list(range(len(places)))
    for way in data["walkways"]:
        assert tuple(way["points_m"][0]) == places[way["from"]] and tuple(way["points_m"][-1]) == places[way["to"]]
        assert way["length_m"] == pytest.approx(LineString(way["points_m"]).length, abs=0.01)
    assert all(math.dist(a, b) >= walkways.MERGE_NODE_M for i, a in enumerate(places) for b in places[i + 1:])


def test_a_crossing_runs_kerb_to_kerb_between_nodes_split_into_the_sidewalks():
    data = walkways.build(CROSSROADS, {}, CROSSING, BUILDING, "test")
    crossing = next(way for way in data["walkways"] if way.get("crossing_id") == "crossing-001")
    assert crossing["control"] == "zebra" and "junction_id" not in crossing
    kerb = walkways.half_width(CROSSROADS[0])
    assert [x for x, _ in crossing["points_m"]] == pytest.approx([30, 30, 30, 30], abs=0.05)
    assert sorted(abs(y) for _, y in crossing["points_m"][1:3]) == pytest.approx([kerb, kerb], abs=0.05), "over the road kerb to kerb"
    for end in (crossing["from"], crossing["to"]):
        assert data["nodes"][end]["kind"] == "kerb"
        assert sum(end in (way["from"], way["to"]) and way["kind"] == "sidewalk" for way in data["walkways"]) == 2, \
            "the sidewalk is split where the crossing steps onto it"
    assert data["report"]["unattached_crossings"] == 0


def test_a_signalled_crossing_carries_the_light_whose_red_lets_people_over():
    data = walkways.build(CROSSROADS, {}, SIGNALLED, BUILDING, "test", JUNCTIONS)
    crossing = next(way for way in data["walkways"] if way.get("crossing_id") == "crossing-001")
    assert (crossing["control"], crossing["junction_id"], crossing["phase"]) == ("signal", "J1", 1)


def test_a_signalled_crossing_whose_light_cannot_be_told_is_left_out_not_left_free():
    data = walkways.build(CROSSROADS, {}, SIGNALLED, BUILDING, "test", [])
    assert not [way for way in data["walkways"] if way.get("crossing_id") == "crossing-001"]
    assert data["report"]["unmatched_signal_crossings"] == 1
    assert data["report"]["unmatched_signal_crossing_ids"] == ["crossing-001"]


def test_side_streets_get_a_crossing_without_a_light_but_not_near_a_mapped_crossing_of_the_same_road():
    bare = walkways.build(CROSSROADS, {}, [], BUILDING, "test")
    across = {way["crossing_id"]: way for way in bare["walkways"] if way["kind"] == "crossing"}
    # Over both residential arms, where the primary road's footpaths go by; never over the primary road
    assert set(across) == {"uncontrolled/way/1/1/1", "uncontrolled/way/1/2/0"}
    offset = walkways.walk_offset(CROSSROADS[2])
    assert sorted(way["points_m"][0][0] for way in across.values()) == pytest.approx([-offset, offset], abs=0.3)
    assert all(way["control"] == "uncontrolled" for way in across.values())
    assert bare["report"]["uncontrolled_ramps"] == 4, "a dropped kerb at each end"
    # crossing-001 is 30 m along the eastern arm, so only the western one keeps its own
    mapped = walkways.build(CROSSROADS, {}, CROSSING, BUILDING, "test")
    assert {way["crossing_id"] for way in mapped["walkways"] if way.get("control") == "uncontrolled"} == {"uncontrolled/way/1/1/1"}
    assert mapped["report"]["ramps"] == 2, "LTA's ramps are counted on their own"


def test_dead_ends_join_round_the_end_of_the_road():
    data = walkways.build([road()], {}, [], [], "test")
    links = [way for way in data["walkways"] if way["kind"] == "link"]
    assert len(links) == 2, "one round each end"
    for link in links:
        assert not LineString(link["points_m"]).intersects(asphalt([road()]).buffer(-0.1))
    assert data["report"]["components"] == 1


def test_a_reversed_one_ways_sidewalk_is_on_the_side_traffic_sees_it():
    one_way = road(oneway=True)
    data = walkways.build([one_way], {1: {"sidewalk": "left", "oneway": "-1"}}, [], [], "test")
    sides = {(way["road_id"], way["side"]) for way in data["walkways"] if way["kind"] == "sidewalk"}
    assert sides == {("way/1/1", "right")}
    assert all(y < 0 for line in walk_lines(data, "sidewalk") for _, y in line.coords)


def test_walkers_start_spread_by_length_and_never_on_a_crossing():
    data = walkways.build(CROSSROADS, {}, CROSSING, BUILDING, "test")
    seeds, ways = data["seeds"], {way["id"]: way for way in data["walkways"]}
    assert len(seeds) == walkways.SEED_COUNT and len({seed["id"] for seed in seeds}) == len(seeds)
    for seed in seeds:
        way = ways[seed["walkway"]]
        assert way["kind"] != "crossing" and 0 <= seed["distance_m"] <= way["length_m"]
        assert walkways.SEED_SPEED_MPS[0] <= seed["speed_mps"] <= walkways.SEED_SPEED_MPS[1]
        assert walkways.SEED_HEIGHT_M[0] <= seed["height_m"] <= walkways.SEED_HEIGHT_M[1]
        assert 0 <= seed["model"] < walkways.SEED_MODELS
    # Spread by length: the longest walkway gets about its share
    walked = [way for way in data["walkways"] if way["kind"] != "crossing"]
    longest = max(walked, key=lambda way: way["length_m"])
    share = longest["length_m"] / sum(way["length_m"] for way in walked)
    assert sum(seed["walkway"] == longest["id"] for seed in seeds) == pytest.approx(share * len(seeds), rel=0.3)


def test_a_network_in_pieces_is_refused():
    with pytest.raises(ValueError, match="largest connected part"):
        walkways.check_network({"report": {"main_component_share": walkways.MIN_MAIN_SHARE - 0.01, "components": 9}})
    walkways.check_network({"report": {"main_component_share": walkways.MIN_MAIN_SHARE, "components": 1}})


def test_the_committed_network_holds_together_and_its_walkers_start_on_its_main_part(committed):
    report = committed["report"]
    assert report["main_component_share"] >= walkways.MIN_MAIN_SHARE
    assert report["seeds"] == len(committed["seeds"]) == walkways.SEED_COUNT
    part = parts_of(committed)
    lengths = {}
    for way in committed["walkways"]:
        lengths[part[way["from"]]] = lengths.get(part[way["from"]], 0) + way["length_m"]
    main = max(lengths, key=lengths.get)
    assert lengths[main] / sum(lengths.values()) == pytest.approx(report["main_component_share"], abs=1e-3)
    assert len(lengths) == report["components"]
    ways = {way["id"]: way for way in committed["walkways"]}
    assert all(part[ways[seed["walkway"]]["from"]] == main and ways[seed["walkway"]]["kind"] != "crossing"
               for seed in committed["seeds"])


def test_every_committed_lta_crossing_is_walked_or_accounted_for(committed):
    """Each LTA crossing is a crossing walkway, has nowhere to land, or has a light that cannot be told: never free to cross."""
    report = committed["report"]
    lta = json.loads(output_path("toa-payoh", "crossings").read_text(encoding="utf-8"))["crossings"]
    walked = {way["crossing_id"]: way for way in committed["walkways"]
              if way["kind"] == "crossing" and way["control"] != "uncontrolled"}
    left = set(report["unattached_crossing_ids"]) | set(report["unmatched_signal_crossing_ids"])
    assert set(walked) | left == {crossing["id"] for crossing in lta} and not set(walked) & left
    assert report["unattached_crossings"] == len(report["unattached_crossing_ids"])
    signals = {junction["id"]: {approach["phase"] for approach in junction["approaches"]}
               for junction in json.loads(output_path("toa-payoh", "signals").read_text(encoding="utf-8"))["junctions"]}
    for crossing in lta:
        if crossing["id"] in walked:
            way = walked[crossing["id"]]
            if crossing["control"] == "signal":
                assert way["control"] == "signal" and way["phase"] in signals[way["junction_id"]]
            else:
                assert way["control"] == "zebra" and "junction_id" not in way
    assert len(walked) >= 0.6 * len(lta)


def test_the_committed_walkways_keep_to_the_paving_and_off_the_road(committed):
    roads, _, _, _ = walkways.inputs("toa-payoh")
    road_area = asphalt(roads).buffer(-0.1)
    area = paved(committed).buffer(0.05)
    sidewalks = shapely.union_all(walk_lines(committed, "sidewalk"))
    assert sidewalks.difference(area).length < 0.001 * sidewalks.length
    assert shapely.union_all(walk_lines(committed, "link")).intersection(road_area).length < 0.5
    places = [tuple(node["xy_m"]) for node in committed["nodes"]]
    for way in committed["walkways"]:
        assert tuple(way["points_m"][0]) == places[way["from"]] and tuple(way["points_m"][-1]) == places[way["to"]]
