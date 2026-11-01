import json
import math
import random

import pytest

from src.routing.build_singapore_fleet_fixture import SERVICE_AREAS, extract_path
from src.traffic.import_vehicle_population import (
    MODELS,
    OUTPUT_DIRECTORY,
    SLOT_M,
    area_box_m,
    build,
    download_rows,
    main,
    model_for,
    place_vehicles,
    point_along,
    population_of_year,
    road_network,
    sample_size,
)

BOX = SERVICE_AREAS["toa-payoh"]["box"]
ROWS = [
    {"year": "2023", "category": "Cars and Station-wagons", "type": "Private cars", "number": "1000"},
    {"year": "2024", "category": "Cars and Station-wagons", "type": "Private cars", "number": "500000"},
    {"year": "2024", "category": "Taxis", "type": "Taxis", "number": "10000"},
    {"year": "2024", "category": "Buses", "type": "Omnibuses", "number": "6000"},
    {"year": "2024", "category": "Goods and Other Vehicles", "type": "Very Heavy Goods Vehicles (VHGVs)", "number": "400"},
    {"year": "2024", "category": "Tax Exempted Vehicles", "type": "Buses", "number": "0"},
]


def osm():
    return json.loads(extract_path("toa-payoh").read_text(encoding="utf-8"))


def test_every_row_ever_published_has_a_model():
    rows = json.loads((OUTPUT_DIRECTORY / "vehicle-population.json").read_text(encoding="utf-8"))["rows"]
    assert len(rows) >= 412
    assert {model_for(row["category"], row["type"]) for row in rows} == set(MODELS)


def test_models_follow_the_type_not_just_the_category():
    assert model_for("Goods and Other Vehicles", "Very Heavy Goods Vehicles (VHGVs)") == "heavy_truck"
    assert model_for("Goods and Other Vehicles", "Heavy Goods Vehicles (HGVs)") == "truck"
    assert model_for("Goods and Other Vehicles", "Light Goods Vehicles (LGVs)") == "van"
    assert model_for("Tax Exempted Vehicles", "Motorcycles and scooters") == "motorcycle"
    assert model_for("Buses", "Omnibuses") == "bus"
    with pytest.raises(ValueError):
        model_for("Hovercraft", "Hovercraft")


def test_a_type_is_scaled_but_a_rare_one_is_never_lost():
    assert sample_size(500_000, 0.1) == 500
    assert sample_size(377, 0.1) == 1
    assert sample_size(0, 0.1) == 0


def test_the_latest_year_is_used_unless_one_is_asked_for():
    assert population_of_year(ROWS, None)[0] == 2024
    assert population_of_year(ROWS, 2023)[1][0]["population"] == 1000
    with pytest.raises(ValueError):
        population_of_year(ROWS, 1999)


def test_the_download_reads_every_page():
    pages = {0: {"records": [{"n": 1}, {"n": 2}], "total": 3}, 2: {"records": [{"n": 3}], "total": 3}}
    assert [row["n"] for row in download_rows(pages.__getitem__)] == [1, 2, 3]
    with pytest.raises(ValueError):
        download_rows(lambda offset: {"records": [], "total": 3})


def test_roads_split_at_junctions_and_follow_one_way_direction():
    nodes = [{"type": "node", "id": i, "lat": 1.33, "lon": 103.85 + i * 0.001} for i in range(1, 5)]
    nodes.append({"type": "node", "id": 5, "lat": 1.331, "lon": 103.852})
    ways = [
        {"type": "way", "id": 1, "nodes": [1, 2, 3, 4], "tags": {"highway": "primary", "lanes": "4", "maxspeed": "60"}},
        {"type": "way", "id": 2, "nodes": [5, 2], "tags": {"highway": "residential", "oneway": "-1"}},
        {"type": "way", "id": 3, "nodes": [1, 5], "tags": {"highway": "service", "access": "private"}},
        {"type": "way", "id": 4, "nodes": [1, 5], "tags": {"highway": "footway"}},
    ]
    roads = road_network({"elements": nodes + ways}, BOX)
    assert [(road["from_node"], road["to_node"]) for road in roads] == [(1, 2), (2, 4), (2, 5)]
    assert roads[0]["lanes"] == 2 and roads[0]["speed_kph"] == 60 and not roads[0]["oneway"]
    assert roads[2]["oneway"] and roads[2]["lanes"] == 1 and roads[2]["speed_kph"] == 40


def test_a_position_on_a_road_is_on_the_left_of_travel():
    assert point_along([[0, 0], [100, 0]], 30, 2) == (30, 2, 0)
    assert point_along([[100, 0], [0, 0]], 30, 2) == (70, -2, 180)
    assert point_along([[0, 0], [0, 10], [10, 10]], 15, 0) == (5, 10, 0)
    with pytest.raises(ValueError):
        point_along([[0, 0], [10, 0]], 11, 0)


def test_the_area_gets_each_type_on_real_roads_inside_its_box():
    data = build(ROWS, osm(), "toa-payoh", 0.1, None, 9, "2026-10-31")
    assert data["dataset"]["year"] == 2024 and data["sampling"]["vehicle_count"] == len(data["vehicles"]) == 500 + 10 + 6 + 1
    west, south, east, north = data["area"]["box_m"]
    roads = {road["id"]: road for road in data["roads"]}
    for vehicle in data["vehicles"]:
        assert west <= vehicle["x_m"] <= east and south <= vehicle["y_m"] <= north
        assert vehicle["model"] in data["models"] and vehicle["road"] in roads
        road = roads[vehicle["road"]]
        assert vehicle["speed_kph"] == road["speed_kph"] and vehicle["lane"] < road["lanes"]
        assert vehicle["direction"] == 1 or not road["oneway"]
    assert len({vehicle["id"] for vehicle in data["vehicles"]}) == len(data["vehicles"])
    assert not [v for v in data["vehicles"] if v["model"] in ("bus", "heavy_truck") and roads[v["road"]]["class"] == "service"]


def test_no_two_vehicles_in_a_lane_overlap():
    data = build(ROWS, osm(), "toa-payoh", 0.5, None, 9, "2026-10-31")
    lanes = {}
    for vehicle in data["vehicles"]:
        lanes.setdefault((vehicle["road"], vehicle["direction"], vehicle["lane"]), []).append(vehicle)
    for vehicles in lanes.values():
        vehicles.sort(key=lambda v: v["distance_m"])
        for a, b in zip(vehicles, vehicles[1:]):
            gap = b["distance_m"] - a["distance_m"] - (MODELS[a["model"]]["length_m"] + MODELS[b["model"]]["length_m"]) / 2
            assert gap > 0
    assert max(model["length_m"] for model in MODELS.values()) < SLOT_M


def test_vehicles_on_different_roads_do_not_overlap_where_the_roads_meet():
    vehicles = build(ROWS, osm(), "toa-payoh", 0.5, None, 9, "2026-10-31")["vehicles"]
    for index, a in enumerate(vehicles):
        for b in vehicles[index + 1:]:
            if a["road"] != b["road"]:
                reach = (MODELS[a["model"]]["length_m"] + MODELS[b["model"]]["length_m"]) / 2
                assert math.dist((a["x_m"], a["y_m"]), (b["x_m"], b["y_m"])) > reach


def test_too_much_traffic_for_the_roads_is_refused():
    west, south, _, _ = area_box_m(BOX)
    road = {"id": "r", "class": "primary", "oneway": True, "lanes": 1, "speed_kph": 50,
            "points_m": [[west + 100, south + 100], [west + 200, south + 100]]}
    types = [{"category": "c", "type": "t", "model": "car", "count": 2}]
    assert len(place_vehicles(types, [road], BOX, random.Random(1))) == 2
    types[0]["count"] = 50
    with pytest.raises(ValueError):
        place_vehicles(types, [road], BOX, random.Random(1))


def test_large_vehicles_keep_off_service_roads_even_if_that_leaves_no_room():
    west, south, _, _ = area_box_m(BOX)
    road = {"id": "r", "class": "service", "oneway": True, "lanes": 1, "speed_kph": 20,
            "points_m": [[west + 100, south + 100], [west + 200, south + 100]]}
    with pytest.raises(ValueError):
        place_vehicles([{"category": "c", "type": "t", "model": "bus", "count": 1}], [road], BOX, random.Random(1))


def test_a_rebuild_is_repeatable_and_a_new_seed_is_not(tmp_path, capsys):
    first, second = tmp_path / "a.json", tmp_path / "b.json"
    assert main(["--offline", "--output", str(first)]) == 0
    assert main(["--offline", "--output", str(second)]) == 0
    assert first.read_bytes() == second.read_bytes()
    assert main(["--offline", "--seed", "10", "--output", str(second)]) == 0
    assert first.read_bytes() != second.read_bytes()
    assert "Wrote" in capsys.readouterr().out


def test_the_committed_traffic_matches_a_rebuild_and_represents_every_type(tmp_path):
    rebuilt = tmp_path / "toa-payoh.json"
    assert main(["--offline", "--output", str(rebuilt)]) == 0
    committed = OUTPUT_DIRECTORY / "toa-payoh.json"
    assert rebuilt.read_text(encoding="utf-8") == committed.read_text(encoding="utf-8")
    data = json.loads(committed.read_text(encoding="utf-8"))
    assert all(row["count"] >= 1 for row in data["types"])
    assert sum(row["count"] for row in data["types"]) == len(data["vehicles"])
    expected = data["sampling"]["national_population"] * data["sampling"]["percent"] / 100
    assert math.isclose(len(data["vehicles"]), expected, rel_tol=0.02)


def test_the_api_serves_the_prepared_roads():
    from fastapi.testclient import TestClient

    from src.app import app

    client = TestClient(app)
    answer = client.get("/api/roads/toa-payoh").json()
    assert len(answer["roads"]) > 1000 and "OpenStreetMap" in answer["attribution"]
    road = answer["roads"][0]
    assert road["lanes"] >= 1 and len(road["points_m"]) >= 2
    assert client.get("/api/roads/nowhere").status_code == 404
    assert client.get("/api/roads/vehicle-population").status_code == 404, "another file in the folder, not an area"
    assert client.get("/api/roads/..%2F..%2Fsecret").status_code == 404


def test_the_api_serves_the_prepared_traffic():
    from fastapi.testclient import TestClient

    from src.app import app

    client = TestClient(app)
    answer = client.get("/api/traffic/toa-payoh").json()
    assert "attribution" in answer and len(answer["attribution"]) >= 1
    assert set(answer["models"]) >= {"car", "taxi", "motorcycle", "van", "truck", "heavy_truck", "bus"}
    assert len(answer["vehicles"]) >= 1000
    assert all("model" in vehicle and "speed_kph" in vehicle for vehicle in answer["vehicles"][:10])
    assert client.get("/api/traffic/nowhere").status_code == 404
    assert client.get("/api/traffic/vehicle-population").status_code == 404
    assert client.get("/api/traffic/..%2F..%2Fsecret").status_code == 404
