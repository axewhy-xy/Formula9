"""Road-graph and independent route-preview tests."""

from pathlib import Path
import sqlite3
import sys

from fastapi.testclient import TestClient
import pytest


BACKEND_DIR = Path(__file__).resolve().parents[1]
if str(BACKEND_DIR) not in sys.path:
    sys.path.insert(0, str(BACKEND_DIR))

from src.app import app
from src.routing.road_graph import RoadGraphRepository
from src.schemas.route import CreateRouteRequest
from src.services.route_planner import (
    RoutePlanNotFoundError,
    RoutePlanStore,
    RouteNotFoundError,
    RoutePlanner,
    SceneRevisionMismatchError,
    get_route_plan_store,
)
from src.services.scene_repository import SceneRepository
from src.services.session_manager import session_manager


def _create_route_scene(path: Path, *, include_osm_metadata: bool = True) -> None:
    with sqlite3.connect(path) as connection:
        connection.executescript(
            """
            CREATE TABLE nodes (node_id INTEGER PRIMARY KEY, lat REAL, lon REAL);
            CREATE TABLE roads (road_id INTEGER PRIMARY KEY, highway_type TEXT);
            CREATE TABLE road_nodes (road_id INTEGER, node_id INTEGER, sequence INTEGER);
            CREATE TABLE buildings (building_id INTEGER PRIMARY KEY, building_type TEXT);
            CREATE TABLE building_nodes (
                building_id INTEGER, node_id INTEGER, sequence INTEGER, lat REAL, lon REAL
            );
            CREATE TABLE pois (
                poi_id INTEGER PRIMARY KEY, lat REAL, lon REAL,
                poi_type TEXT, poi_value TEXT, name TEXT
            );

            INSERT INTO nodes VALUES
                (1, 1.0000, 103.0000),
                (2, 1.0000, 103.0010),
                (3, 1.0000, 103.0020);
            INSERT INTO roads VALUES (10, 'primary'), (11, 'residential');
            INSERT INTO road_nodes VALUES
                (10, 1, 0), (10, 2, 1),
                (11, 2, 0), (11, 3, 1);
            """
        )
    if include_osm_metadata:
        path.with_name("map.osm").write_text(
            """<?xml version="1.0" encoding="UTF-8"?>
            <osm version="0.6">
              <way id="10">
                <nd ref="1"/><nd ref="2"/>
                <tag k="highway" v="primary"/>
                <tag k="oneway" v="yes"/>
                <tag k="maxspeed" v="36"/>
              </way>
              <way id="11">
                <nd ref="2"/><nd ref="3"/>
                <tag k="highway" v="residential"/>
                <tag k="access" v="permissive"/>
              </way>
            </osm>
            """,
            encoding="utf-8",
        )


def _planner(database: Path) -> tuple[RoutePlanner, RoadGraphRepository, SceneRepository]:
    graph_repository = RoadGraphRepository(database)
    scene_repository = SceneRepository(database)
    return (
        RoutePlanner(scene_repository, graph_repository),
        graph_repository,
        scene_repository,
    )


def _request(
    scene_repository: SceneRepository,
    graph_repository: RoadGraphRepository,
    start_node: int,
    destination_node: int,
) -> CreateRouteRequest:
    graph = graph_repository.get().graph
    start = graph.nodes[start_node]
    destination = graph.nodes[destination_node]
    return CreateRouteRequest(
        scene_revision=scene_repository.metadata().scene_revision,
        start={"x": start["x"], "y": start["y"]},
        destination={"x": destination["x"], "y": destination["y"]},
    )


def test_graph_preserves_direction_length_access_type_and_speed(tmp_path: Path) -> None:
    database = tmp_path / "map.db"
    _create_route_scene(database)
    repository = RoadGraphRepository(database)

    first = repository.get()
    second = repository.get()
    graph = first.graph
    edge = next(iter(graph.get_edge_data(1, 2).values()))
    permissive_edge = next(iter(graph.get_edge_data(2, 3).values()))

    assert first.routing_mode == "directed"
    assert second.graph is graph
    assert graph.has_edge(1, 2)
    assert not graph.has_edge(2, 1)
    assert edge["source"] == 1
    assert edge["target"] == 2
    assert edge["length_m"] > 100.0
    assert edge["oneway"] is True
    assert edge["highway_type"] == "primary"
    assert edge["access_restrictions"] == {}
    assert edge["speed_estimate_kph"] == pytest.approx(36.0)
    assert permissive_edge["access_restrictions"] == {"access": "permissive"}


def test_private_road_is_routable_for_the_authorized_simulation(
    tmp_path: Path,
) -> None:
    database = tmp_path / "map.db"
    _create_route_scene(database)
    with sqlite3.connect(database) as connection:
        connection.execute("INSERT INTO nodes VALUES (4, 1.0000, 103.0030)")
        connection.execute("INSERT INTO roads VALUES (12, 'service')")
        connection.executemany(
            "INSERT INTO road_nodes VALUES (?, ?, ?)",
            ((12, 3, 0), (12, 4, 1)),
        )
    osm_path = database.with_name("map.osm")
    osm_path.write_text(
        osm_path.read_text(encoding="utf-8").replace(
            "</osm>",
            """  <way id="12">
                <nd ref="3"/><nd ref="4"/>
                <tag k="highway" v="service"/>
                <tag k="motor_vehicle" v="private"/>
              </way>
            </osm>""",
        ),
        encoding="utf-8",
    )

    planner, graph_repository, scene_repository = _planner(database)
    graph = graph_repository.get().graph
    private_edge = next(iter(graph.get_edge_data(3, 4).values()))

    assert graph.has_edge(3, 4)
    assert graph.has_edge(4, 3)
    assert private_edge["access_restrictions"] == {"motor_vehicle": "private"}
    response = planner.plan(_request(scene_repository, graph_repository, 1, 4))
    assert response.snapped_destination.node_id == 4


def test_legacy_database_is_clearly_labeled_undirected_prototype(tmp_path: Path) -> None:
    database = tmp_path / "map.db"
    _create_route_scene(database, include_osm_metadata=False)

    snapshot = RoadGraphRepository(database).get()

    assert snapshot.routing_mode == "undirected_prototype"
    assert snapshot.graph.has_edge(1, 2)
    assert snapshot.graph.has_edge(2, 1)
    assert "may go against one-way traffic" in snapshot.warnings[0]


def test_planner_snaps_and_returns_directed_shortest_path(tmp_path: Path) -> None:
    database = tmp_path / "map.db"
    _create_route_scene(database)
    planner, graph_repository, scene_repository = _planner(database)

    response = planner.plan(_request(scene_repository, graph_repository, 1, 3))

    assert response.routing_mode == "directed"
    assert response.snapped_start.node_id == 1
    assert response.snapped_destination.node_id == 3
    assert response.snapped_start.snapping_distance == 0.0
    assert [point.x for point in response.polyline] == pytest.approx(
        [graph_repository.get().graph.nodes[node]["x"] for node in (1, 2, 3)]
    )
    assert response.distance > 200.0
    assert response.estimated_duration > 0.0
    assert response.scene_revision == scene_repository.metadata().scene_revision


def test_planner_retains_a_bounded_route_for_simulation_creation(tmp_path: Path) -> None:
    database = tmp_path / "map.db"
    _create_route_scene(database)
    graph_repository = RoadGraphRepository(database)
    scene_repository = SceneRepository(database)
    route_store = RoutePlanStore(maximum_routes=1)
    planner = RoutePlanner(scene_repository, graph_repository, route_store)

    first = planner.plan(_request(scene_repository, graph_repository, 1, 2))
    assert route_store.get(first.route_id).polyline == first.polyline

    second = planner.plan(_request(scene_repository, graph_repository, 1, 3))
    assert route_store.get(second.route_id).route_id == second.route_id
    with pytest.raises(RoutePlanNotFoundError, match="not found or has expired"):
        route_store.get(first.route_id)


def test_planner_rejects_reverse_oneway_and_stale_revision(tmp_path: Path) -> None:
    database = tmp_path / "map.db"
    _create_route_scene(database)
    planner, graph_repository, scene_repository = _planner(database)

    with pytest.raises(RouteNotFoundError):
        planner.plan(_request(scene_repository, graph_repository, 3, 1))

    stale = _request(scene_repository, graph_repository, 1, 3).model_copy(
        update={"scene_revision": "stale"}
    )
    with pytest.raises(SceneRevisionMismatchError):
        planner.plan(stale)


def test_route_endpoint_is_independent_from_simulation_creation(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    database = tmp_path / "map.db"
    _create_route_scene(database)
    planner, graph_repository, scene_repository = _planner(database)
    request = _request(scene_repository, graph_repository, 1, 3)
    monkeypatch.setattr(
        "src.api.routes.route_planning.get_route_planner", lambda: planner
    )

    with TestClient(app) as client:
        response = client.post("/api/routes", json=request.model_dump())

    assert response.status_code == 200
    payload = response.json()
    assert payload["contract_version"] == "1.0"
    assert payload["snapped_start"]["node_id"] == 1
    assert payload["snapped_destination"]["node_id"] == 3
    assert payload["polyline"]
    assert payload["distance_unit"] == "m"
    assert payload["duration_unit"] == "s"


def test_route_preview_can_create_and_stream_a_simulation(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    database = tmp_path / "map.db"
    _create_route_scene(database)
    graph_repository = RoadGraphRepository(database)
    scene_repository = SceneRepository(database)
    route_store = get_route_plan_store()
    route_store.clear()
    session_manager.clear()
    planner = RoutePlanner(scene_repository, graph_repository, route_store)
    request = _request(scene_repository, graph_repository, 1, 3)
    monkeypatch.setattr(
        "src.api.routes.route_planning.get_route_planner", lambda: planner
    )
    monkeypatch.setattr(
        "src.services.session_manager.get_scene_repository",
        lambda: scene_repository,
    )

    try:
        with TestClient(app) as client:
            route_response = client.post("/api/routes", json=request.model_dump())
            assert route_response.status_code == 200
            route = route_response.json()

            simulation_response = client.post(
                "/api/simulations",
                json={
                    "scenario_name": "planned route",
                    "route_id": route["route_id"],
                    "scene_id": route["scene_id"],
                    "playback_speed": 16.0,
                    "ego_source": "SIMULATION",
                    "alignment_mode": "SIMULATED_SCENE",
                },
            )
            assert simulation_response.status_code == 201
            simulation = simulation_response.json()
            assert simulation["route_id"] == route["route_id"]
            assert simulation["trajectory"]["route_id"] == route["route_id"]

            with client.websocket_connect(simulation["websocket_url"]) as websocket:
                assert websocket.receive_json()["message_type"] == "SESSION_READY"
                metadata = websocket.receive_json()
                assert metadata["trajectory"]["route_id"] == route["route_id"]
                websocket.send_json(
                    {
                        "message_type": "START",
                        "simulation_id": simulation["simulation_id"],
                    }
                )
                tick = websocket.receive_json()
                assert tick["message_type"] == "TICK"
                assert tick["ego"]["position"] == route["polyline"][0]
    finally:
        session_manager.clear()
        route_store.clear()
