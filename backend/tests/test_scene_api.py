"""Static-scene service and REST contract tests."""

from pathlib import Path
import sqlite3
import sys

from fastapi.testclient import TestClient


BACKEND_DIR = Path(__file__).resolve().parents[1]
if str(BACKEND_DIR) not in sys.path:
    sys.path.insert(0, str(BACKEND_DIR))

from src.app import app
from src.schemas.scene import SceneBounds
from src.services.scene_repository import SceneRepository


def _create_scene_database(path: Path) -> None:
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

            INSERT INTO nodes VALUES (1, 1.0, 103.0), (2, 1.001, 103.001);
            INSERT INTO roads VALUES (10, 'primary');
            INSERT INTO road_nodes VALUES (10, 1, 0), (10, 2, 1);
            INSERT INTO buildings VALUES (20, 'commercial');
            INSERT INTO building_nodes VALUES
                (20, 3, 0, 1.0002, 103.0002),
                (20, 4, 1, 1.0002, 103.0004),
                (20, 5, 2, 1.0004, 103.0004);
            INSERT INTO pois VALUES (30, 1.0003, 103.0003, 'amenity', 'cafe', 'Test Cafe');
            """
        )


def test_repository_projects_sqlite_features_to_local_metres(tmp_path: Path) -> None:
    database = tmp_path / "map.db"
    _create_scene_database(database)
    repository = SceneRepository(database)

    metadata = repository.metadata()
    chunk = repository.chunk(
        "default",
        SceneBounds(min_x=-1000, min_y=-1000, max_x=1000, max_y=1000),
        lod=3,
        include_buildings=True,
        include_pois=True,
    )

    assert metadata.coordinates.x_axis == "east"
    assert metadata.coordinates.y_axis == "north"
    assert metadata.coordinates.z_axis == "up"
    assert metadata.transform_revision == "wgs84-linear-enu-v1"
    assert metadata.counts.model_dump() == {"roads": 1, "buildings": 1, "pois": 1}
    assert len(chunk.roads) == 1
    assert len(chunk.roads[0].points) == 2
    assert chunk.roads[0].points[1].x > chunk.roads[0].points[0].x
    assert chunk.roads[0].points[1].y > chunk.roads[0].points[0].y
    assert len(chunk.buildings) == 1
    assert chunk.pois[0].name == "Test Cafe"


def test_scene_rest_endpoint_returns_versioned_local_scene() -> None:
    with TestClient(app) as client:
        metadata_response = client.get("/api/scenes/default")
        assert metadata_response.status_code == 200
        metadata = metadata_response.json()
        bounds = metadata["bounds"]

        chunk_response = client.get(
            "/api/scenes/default/chunks",
            params={
                "min_x": bounds["min_x"] - 1,
                "min_y": bounds["min_y"] - 1,
                "max_x": bounds["max_x"] + 1,
                "max_y": bounds["max_y"] + 1,
                "lod": 3,
            },
        )

    assert chunk_response.status_code == 200
    chunk = chunk_response.json()
    assert chunk["scene_revision"] == metadata["scene_revision"]
    assert chunk["transform_revision"] == metadata["transform_revision"]
    assert chunk["coordinates"] == metadata["coordinates"]
    assert chunk["roads"]
    assert chunk["buildings"]
    assert chunk["pois"]


def test_scene_chunk_rejects_inverted_bounds() -> None:
    with TestClient(app) as client:
        response = client.get(
            "/api/scenes/default/chunks",
            params={"min_x": 10, "min_y": 0, "max_x": -10, "max_y": 20},
        )

    assert response.status_code == 422


def test_scene_chunk_keeps_a_road_crossing_the_viewport(tmp_path: Path) -> None:
    """A road is visible even when both of its endpoints are outside the bounds."""

    database = tmp_path / "map.db"
    _create_scene_database(database)
    with sqlite3.connect(database) as connection:
        connection.executescript(
            """
            INSERT INTO nodes VALUES
                (3, 1.0005, 102.999),
                (4, 1.0005, 103.002);
            INSERT INTO roads VALUES (11, 'primary');
            INSERT INTO road_nodes VALUES (11, 3, 0), (11, 4, 1);
            """
        )

    repository = SceneRepository(database)
    chunk = repository.chunk(
        "default",
        SceneBounds(min_x=-10, min_y=-10, max_x=10, max_y=10),
        lod=3,
        include_buildings=False,
        include_pois=False,
    )

    assert 11 in {road.road_id for road in chunk.roads}


def test_scene_associated_demo_uses_the_scene_coordinate_origin() -> None:
    with TestClient(app) as client:
        scene = client.get("/api/scenes/default").json()
        response = client.post(
            "/api/simulations",
            json={"scenario_name": "native", "scene_id": "default"},
        )

    assert response.status_code == 201
    simulation = response.json()
    assert simulation["scene_id"] == "default"
    assert simulation["scene_revision"] == scene["scene_revision"]
    assert simulation["transform_revision"] == scene["transform_revision"]
    assert simulation["ego_source"] == "SIMULATION"
    assert simulation["alignment_mode"] == "SIMULATED_SCENE"
    assert simulation["coordinates"]["origin"] == scene["coordinates"]["origin"]


def test_scene_and_live_tick_share_one_canonical_coordinate_contract() -> None:
    """Both presentation clients can combine REST scene data with WebSocket ticks."""

    with TestClient(app) as client:
        scene = client.get("/api/scenes/default").json()
        created = client.post(
            "/api/simulations",
            json={
                "scenario_name": "dual-renderer parity",
                "scene_id": "default",
                "playback_speed": 16.0,
            },
        ).json()

        with client.websocket_connect(created["websocket_url"]) as websocket:
            ready = websocket.receive_json()
            trajectory = websocket.receive_json()
            websocket.send_json(
                {
                    "message_type": "START",
                    "simulation_id": created["simulation_id"],
                }
            )
            tick = websocket.receive_json()
            websocket.send_json(
                {
                    "message_type": "STOP",
                    "simulation_id": created["simulation_id"],
                }
            )

        client.delete(f"/api/simulations/{created['simulation_id']}")

    assert ready["message_type"] == "SESSION_READY"
    assert trajectory["message_type"] == "TRAJECTORY_METADATA"
    assert tick["message_type"] == "TICK"
    assert created["coordinates"] == scene["coordinates"]
    assert ready["coordinates"] == scene["coordinates"]
    assert trajectory["coordinates"] == scene["coordinates"]
    assert tick["coordinates"] == scene["coordinates"]
