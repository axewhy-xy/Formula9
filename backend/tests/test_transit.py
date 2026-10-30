"""LTA DataMall import, transit store and /api/transit endpoint tests."""

from pathlib import Path
import sqlite3
import sys

import pytest
from fastapi.testclient import TestClient

BACKEND_DIR = Path(__file__).resolve().parents[1]
if str(BACKEND_DIR) not in sys.path:
    sys.path.insert(0, str(BACKEND_DIR))

from src.api.routes.transit import get_transit_store
from src.app import app
from src.routing.build_singapore_map import write_database as write_road_database
from src.transit.import_lta import PAGE_SIZE, DataMallError, fetch_all
from src.transit.store import TransitStore, TransitUnavailable, write_database

STOPS = [
    {"BusStopCode": "01012", "RoadName": "Victoria St", "Description": "Hotel Grand Pacific",
     "Latitude": 1.29684825487647, "Longitude": 103.85253591654006},
    {"BusStopCode": "01013", "RoadName": "Victoria St", "Description": "St. Joseph's Ch",
     "Latitude": 1.29770970610083, "Longitude": 103.8532247463225},
    {"BusStopCode": "75009", "RoadName": "Tampines Ctrl 1", "Description": "Tampines Int",
     "Latitude": 1.35406, "Longitude": 103.94339},
    # DataMall lists a few placeholders at 0, 0; they must not reach the map
    {"BusStopCode": "99999", "RoadName": "", "Description": "Unknown", "Latitude": 0, "Longitude": 0},
]
SERVICES = [
    {"ServiceNo": "10", "Operator": "SBST", "Direction": 1, "Category": "TRUNK", "OriginCode": "75009",
     "DestinationCode": "01012", "LoopDesc": ""},
]
ROUTES = [
    {"ServiceNo": "10", "Operator": "SBST", "Direction": 1, "StopSequence": 2, "BusStopCode": "01012",
     "Distance": 20.1},
    {"ServiceNo": "10", "Operator": "SBST", "Direction": 1, "StopSequence": 1, "BusStopCode": "75009",
     "Distance": 0},
]


@pytest.fixture
def transit_path(tmp_path: Path) -> Path:
    path = tmp_path / "transit.sqlite"
    write_database(path, STOPS, SERVICES, ROUTES, "2026-10-30T00:00:00+00:00")
    return path


@pytest.fixture
def client(transit_path: Path):
    app.dependency_overrides[get_transit_store] = lambda: TransitStore(transit_path)
    try:
        yield TestClient(app)
    finally:
        app.dependency_overrides.pop(get_transit_store, None)


def test_fetch_all_pages_until_a_short_page():
    requested = []

    def fetch(url, key):
        requested.append(url)
        if len(requested) < 3:
            return [{"n": len(requested)}] * PAGE_SIZE
        return [{"n": 3}] * 7

    rows = fetch_all("BusStops", "key", fetch)
    assert len(rows) == 2 * PAGE_SIZE + 7
    assert [url.rsplit("=", 1)[1] for url in requested] == ["0", "500", "1000"]


def test_fetch_all_stops_on_endless_paging():
    with pytest.raises(DataMallError):
        fetch_all("BusRoutes", "key", lambda url, key: [{}] * PAGE_SIZE)


def test_write_skips_stops_without_coordinates(transit_path: Path):
    with sqlite3.connect(transit_path) as connection:
        codes = [row[0] for row in connection.execute("SELECT code FROM bus_stops ORDER BY code")]
    assert codes == ["01012", "01013", "75009"]
    assert not transit_path.with_name("transit.sqlite.partial").exists()


def test_stops_in_box_and_truncation(transit_path: Path):
    store = TransitStore(transit_path)
    city = store.stops_in(1.29, 103.85, 1.30, 103.86, limit=10)
    assert [stop["code"] for stop in city["stops"]] == ["01012", "01013"]
    assert city["truncated"] is False
    assert "LTA DataMall" in city["attribution"]
    capped = store.stops_in(1.29, 103.85, 1.30, 103.86, limit=1)
    assert len(capped["stops"]) == 1 and capped["truncated"] is True


def test_route_is_in_stop_order(transit_path: Path):
    route = TransitStore(transit_path).route("10", 1)
    assert [stop["code"] for stop in route["stops"]] == ["75009", "01012"]
    assert TransitStore(transit_path).route("10", 2) is None


def test_missing_database_is_unavailable(tmp_path: Path):
    with pytest.raises(TransitUnavailable):
        TransitStore(tmp_path / "missing.sqlite").services()


def test_bus_stop_endpoint(client: TestClient):
    response = client.get("/api/transit/bus-stops",
                          params={"south": 1.29, "west": 103.85, "north": 1.36, "east": 103.95})
    assert response.status_code == 200
    assert [stop["code"] for stop in response.json()["stops"]] == ["01012", "01013", "75009"]


def test_bus_stop_endpoint_rejects_an_inverted_box(client: TestClient):
    response = client.get("/api/transit/bus-stops",
                          params={"south": 1.4, "west": 103.85, "north": 1.3, "east": 103.95})
    assert response.status_code == 422


def test_bus_stop_endpoint_outside_singapore_is_empty(client: TestClient):
    response = client.get("/api/transit/bus-stops",
                          params={"south": 49.0, "west": 8.4, "north": 49.1, "east": 8.5})
    assert response.status_code == 200 and response.json()["stops"] == []


def test_service_endpoints(client: TestClient):
    assert client.get("/api/transit/bus-services").json()["services"][0]["service_no"] == "10"
    stops = client.get("/api/transit/bus-services/10/stops").json()["stops"]
    assert [stop["sequence"] for stop in stops] == [1, 2]
    assert client.get("/api/transit/bus-services/999/stops").status_code == 404


def test_endpoint_without_import_answers_503(tmp_path: Path):
    app.dependency_overrides[get_transit_store] = lambda: TransitStore(tmp_path / "missing.sqlite")
    try:
        response = TestClient(app).get("/api/transit/bus-services")
    finally:
        app.dependency_overrides.pop(get_transit_store, None)
    assert response.status_code == 503
    assert "import_lta" in response.json()["detail"]


def test_road_database_matches_the_map_schema(tmp_path: Path, transit_path: Path):
    osm = {"elements": [
        {"type": "way", "id": 5, "nodes": [1, 2, 3], "tags": {"highway": "primary", "oneway": "yes", "name": "Orchard Rd"}},
        # A way whose nodes were all left out of the answer is skipped
        {"type": "way", "id": 6, "nodes": [8, 9], "tags": {"highway": "service"}},
        {"type": "node", "id": 1, "lat": 1.30, "lon": 103.83},
        {"type": "node", "id": 2, "lat": 1.301, "lon": 103.831},
        {"type": "node", "id": 3, "lat": 1.302, "lon": 103.832},
        {"type": "node", "id": 4, "lat": 1.5, "lon": 104.0},
    ]}
    path = tmp_path / "map.db"
    counts = write_road_database(path, osm, transit_path)
    assert counts == {"roads": 1, "nodes": 3, "bus_stops": 3}
    with sqlite3.connect(path) as connection:
        assert connection.execute("SELECT highway_type, oneway, name FROM roads").fetchall() == [("primary", "yes", "Orchard Rd")]
        assert [row[0] for row in connection.execute("SELECT node_id FROM road_nodes ORDER BY sequence")] == [1, 2, 3]
        assert connection.execute("SELECT COUNT(*) FROM nodes_rtree").fetchone()[0] == 3
        assert connection.execute("SELECT name FROM bus_stops WHERE busStop_id = 1012").fetchone()[0] == "Hotel Grand Pacific"


def test_osm_stops_need_a_code_and_keep_their_own_credit(tmp_path):
    from src.transit.import_osm import ATTRIBUTION as OSM_CREDIT, stop_rows
    from src.transit.store import TransitStore, write_database

    rows = stop_rows([
        {"lat": 1.33, "lon": 103.85, "tags": {"ref": "52449", "name": "Toa Payoh Police Ctr", "addr:street": "Lor 6"}},
        {"lat": 1.34, "lon": 103.86, "tags": {"name": "No code"}},
    ])
    assert [row["BusStopCode"] for row in rows] == ["52449"]
    path = tmp_path / "transit.sqlite"
    write_database(path, rows, [], [], "now", OSM_CREDIT)
    found = TransitStore(path).stops_in(1.3, 103.8, 1.4, 103.9, 10)
    assert found["stops"][0]["description"] == "Toa Payoh Police Ctr"
    assert found["attribution"] == OSM_CREDIT
