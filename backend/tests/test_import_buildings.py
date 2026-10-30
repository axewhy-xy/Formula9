import json
import sqlite3

from src.routing.import_buildings import building_row, join_pieces, number, resolved_height, write_database

SQUARE = [{"lat": 1.33, "lon": 103.85}, {"lat": 1.33, "lon": 103.851}, {"lat": 1.331, "lon": 103.851},
          {"lat": 1.331, "lon": 103.85}, {"lat": 1.33, "lon": 103.85}]


def test_a_way_becomes_a_shape_with_coordinates_and_local_metres(tmp_path):
    path = tmp_path / "buildings.sqlite"
    elements = [
        {"type": "way", "id": 1, "tags": {"building": "apartments", "name": "Blk 1", "height": "45 m", "building:levels": "12"}, "geometry": SQUARE},
        {"type": "relation", "id": 2, "tags": {"building": "yes"},
         "members": [{"type": "way", "role": "outer", "geometry": SQUARE}, {"type": "way", "role": "inner", "geometry": SQUARE[:3]}]},
        {"type": "way", "id": 3, "tags": {"building": "yes"}, "geometry": SQUARE[:3]},
    ]
    assert write_database(path, elements, "now") == 2, "an open outline is not a building shape"
    row = sqlite3.connect(path).execute(
        "SELECT building_type, name, height_m, levels, center_lat, outer_rings_m FROM buildings WHERE osm_id = 'way/1'").fetchone()
    assert row[:4] == ("apartments", "Blk 1", 45.0, 12.0)
    assert abs(row[4] - 1.3304) < 0.001
    ring = json.loads(row[5])[0]
    assert len(ring) == 5 and ring[0] == ring[-1]
    assert abs(ring[1][0] - ring[0][0]) > 100, "about 111 m east, in metres not degrees"


def test_heights_are_read_from_loose_osm_values():
    assert number("45") == 45 and number("45.5 m") == 45.5 and number("12;14") == 12
    assert number("tall") is None and number(None) is None
    assert building_row({"type": "way", "id": 9, "tags": {}, "geometry": []}) is None


def test_an_outline_split_across_several_ways_is_joined():
    points = [(1.0, 1.0), (1.0, 2.0), (2.0, 2.0), (2.0, 1.0), (1.0, 1.0)]
    first, second = points[:3], list(reversed(points[2:]))
    assert join_pieces([first, second]) == [points]
    assert join_pieces([points[:3]]) == [points[:3]], "an outline that never closes stays open"


def test_heights_use_real_data_first_then_a_repeatable_guess():
    assert resolved_height("way/1", "apartments", 45.0, 12.0) == 45.0
    assert resolved_height("way/1", "apartments", None, 12.0) == 36.0
    guess = resolved_height("way/1", "house", None, None)
    assert 6 <= guess <= 10 and guess == resolved_height("way/1", "house", None, None)


def test_the_api_serves_the_imported_area():
    from fastapi.testclient import TestClient

    from src.app import app

    client = TestClient(app)
    answer = client.get("/api/buildings/toa-payoh").json()
    assert len(answer["buildings"]) > 1000 and "OpenStreetMap" in answer["attribution"]
    building = answer["buildings"][0]
    assert building["height_m"] > 0 and len(building["rings_m"][0]) >= 4
    assert client.get("/api/buildings/nowhere").status_code == 404
    assert client.get("/api/buildings/..%2F..%2Fsecret").status_code == 404
