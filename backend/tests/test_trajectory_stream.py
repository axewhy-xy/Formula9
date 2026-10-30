"""
Unit tests for session-owned trajectory loading and message generation.

Author: Lau Xiang Yang, CC
Date Created: 20/09/2026
Last Modified: 20/09/2026

Description:
    This module contains unit tests for the trajectory stream functionality, which is responsible for loading
    and generating messages for the trajectory data of each simulation session. The tests ensure that the
    trajectory data is correctly loaded and that the generated messages are properly formatted.

"""

from pathlib import Path
import shutil
import sys
from types import SimpleNamespace

import pytest

from contracts.common import GeographicPoint


BACKEND_DIR = Path(__file__).resolve().parents[1]
if str(BACKEND_DIR) not in sys.path:
    sys.path.insert(0, str(BACKEND_DIR))

from src.schemas.simulation import CreateSimulationRequest
from src.schemas.route import RouteResponse
from src.core.errors import InvalidSimulationSourceError
from src.services.session_manager import SessionManager
from src.services.trajectory_stream import (
    TrajectorySourceError,
    build_planned_route_stream,
    build_trajectory_stream,
)


FIXTURES = Path(__file__).parent / "fixtures"
SEQUENCE_ID = "2011_09_26_drive_test"


def _make_kitti_root(tmp_path: Path) -> Path:
    root = tmp_path / "kitti"
    oxts = root / SEQUENCE_ID / "oxts"
    shutil.copytree(FIXTURES / "sample_oxts", oxts / "data")
    shutil.copyfile(FIXTURES / "timestamps.txt", oxts / "timestamps.txt")
    return root


def _planned_route(scene_revision: str = "scene-revision") -> RouteResponse:
    return RouteResponse(
        route_id="route-test",
        scene_id="default",
        scene_revision=scene_revision,
        transform_revision="wgs84-linear-enu-v1",
        routing_mode="directed",
        snapped_start={"node_id": 1, "x": 0.0, "y": 0.0, "z": 0.0, "snapping_distance": 0.0},
        snapped_destination={"node_id": 3, "x": 10.0, "y": 10.0, "z": 0.0, "snapping_distance": 0.0},
        polyline=[
            {"x": 0.0, "y": 0.0, "z": 0.0},
            {"x": 10.0, "y": 0.0, "z": 0.0},
            {"x": 10.0, "y": 10.0, "z": 0.0},
        ],
        distance=20.0,
        estimated_duration=2.0,
        bounds={"min_x": 0.0, "min_y": 0.0, "max_x": 10.0, "max_y": 10.0},
    )


def test_kitti_stream_uses_normalized_fixture_data(tmp_path: Path) -> None:
    stream = build_trajectory_stream(
        dataset_name="kitti",
        sequence_id=SEQUENCE_ID,
        playback_speed=1.0,
        kitti_root=_make_kitti_root(tmp_path),
    )

    assert stream.metadata.dataset_name == "kitti"
    assert stream.metadata.sequence_id == SEQUENCE_ID
    assert stream.metadata.sample_count == 6
    tick = stream.tick_message("sim_test", 1)
    assert tick["contract_version"] == "1.0"
    assert tick["ego"]["source_frame"] == 1
    assert set(tick["ego"]["position"]) == {"x", "y", "z"}
    assert set(tick["ego"]["orientation"]) == {"heading", "pitch", "roll"}
    assert tick["trajectory"]["current_index"] == 1
    assert tick["trajectory"]["points"]


def test_kitti_stream_uses_requested_scene_coordinate_origin(tmp_path: Path) -> None:
    scene_origin = GeographicPoint(latitude=49.0, longitude=8.4)
    stream = build_trajectory_stream(
        dataset_name="kitti",
        sequence_id=SEQUENCE_ID,
        playback_speed=1.0,
        kitti_root=_make_kitti_root(tmp_path),
        coordinate_origin=scene_origin,
    )

    assert stream.coordinates.origin == scene_origin
    assert stream.samples[0].position.x != pytest.approx(0.0)
    assert stream.samples[0].position.y != pytest.approx(0.0)


def test_tick_output_is_ordered_and_completes() -> None:
    stream = build_trajectory_stream(
        dataset_name=None,
        sequence_id=None,
        playback_speed=2.0,
    )

    messages = [stream.tick_message("sim_test", tick) for tick in range(stream.tick_count)]
    assert [message["tick"] for message in messages] == list(range(stream.tick_count))
    assert [message["simulation_time"] for message in messages] == sorted(
        message["simulation_time"] for message in messages
    )
    complete = stream.complete_message("sim_test", stream.tick_count - 1)
    assert complete["status"] == "COMPLETED"
    assert complete["final_tick"] == stream.tick_count - 1
    assert complete["simulation_time"] == pytest.approx(stream.duration)


def test_planned_route_stream_resamples_positions_heading_and_metadata() -> None:
    stream = build_planned_route_stream(
        route=_planned_route(),
        playback_speed=1.0,
        coordinate_origin=GeographicPoint(latitude=1.3, longitude=103.8),
    )

    assert stream.metadata.route_id == "route-test"
    assert stream.metadata.dataset_name is None
    assert stream.metadata.source_coordinate_system == "planned_route_local_cartesian"
    assert stream.metadata.sample_count == 21
    assert stream.duration == pytest.approx(2.0)
    assert stream.samples[0].position == stream.samples[0].position.__class__(0.0, 0.0, 0.0)
    assert stream.samples[-1].position == stream.samples[-1].position.__class__(10.0, 10.0, 0.0)
    assert stream.tick_message("sim", 0)["ego"]["orientation"]["heading"] == pytest.approx(0.0)
    assert stream.tick_message("sim", 15)["ego"]["orientation"]["heading"] == pytest.approx(90.0)
    assert stream.tick_message("sim", 15)["ego"]["speed"] == pytest.approx(10.0)


def test_session_creation_uses_stored_planned_route(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    route = _planned_route()
    scene = SimpleNamespace(
        scene_revision=route.scene_revision,
        transform_revision=route.transform_revision,
        coordinates=SimpleNamespace(
            origin=SimpleNamespace(latitude=1.3, longitude=103.8)
        ),
        bounds=SimpleNamespace(min_x=-100, min_y=-100, max_x=100, max_y=100),
    )
    monkeypatch.setattr(
        "src.services.session_manager.get_scene_repository",
        lambda: SimpleNamespace(metadata=lambda _scene_id: scene),
    )
    manager = SessionManager(route_resolver=lambda route_id: route)

    response = manager.create(
        CreateSimulationRequest(
            scenario_name="planned test",
            route_id=route.route_id,
            scene_id=route.scene_id,
            playback_speed=2.0,
        )
    )

    session = manager.get_session(response.simulation_id)
    assert response.route_id == route.route_id
    assert response.dataset_name is None
    assert response.ego_source == "SIMULATION"
    assert response.alignment_mode == "SIMULATED_SCENE"
    assert response.trajectory.route_id == route.route_id
    assert response.route_summary.distance == pytest.approx(route.distance)
    assert response.route_summary.estimated_duration == pytest.approx(1.0)
    assert session.stream.samples[0].position.x == pytest.approx(0.0)
    assert session.stream.samples[-1].position.y == pytest.approx(10.0)


def test_planned_route_request_rejects_conflicts_and_stale_revision(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    with pytest.raises(ValueError, match="cannot be supplied together"):
        CreateSimulationRequest(
            dataset_name="kitti",
            sequence_id=SEQUENCE_ID,
            route_id="route-test",
            scene_id="default",
        )
    with pytest.raises(ValueError, match="scene_id is required"):
        CreateSimulationRequest(route_id="route-test")

    route = _planned_route("old-revision")
    scene = SimpleNamespace(
        scene_revision="new-revision",
        transform_revision=route.transform_revision,
        coordinates=SimpleNamespace(
            origin=SimpleNamespace(latitude=1.3, longitude=103.8)
        ),
        bounds=SimpleNamespace(min_x=-100, min_y=-100, max_x=100, max_y=100),
    )
    monkeypatch.setattr(
        "src.services.session_manager.get_scene_repository",
        lambda: SimpleNamespace(metadata=lambda _scene_id: scene),
    )
    manager = SessionManager(route_resolver=lambda _route_id: route)
    with pytest.raises(InvalidSimulationSourceError, match="stale"):
        manager.create(
            CreateSimulationRequest(route_id=route.route_id, scene_id="default")
        )


def test_session_creation_keeps_dataset_configuration(tmp_path: Path) -> None:
    root = _make_kitti_root(tmp_path)

    def stream_factory(**config):
        return build_trajectory_stream(**config, kitti_root=root)

    manager = SessionManager(stream_factory=stream_factory)
    response = manager.create(
        CreateSimulationRequest(
            scenario_name="fixture replay",
            dataset_name="kitti",
            sequence_id=SEQUENCE_ID,
            playback_speed=2.0,
        )
    )

    assert response.dataset_name == "kitti"
    assert response.sequence_id == SEQUENCE_ID
    assert response.playback_speed == 2.0
    assert response.ego_source == "KITTI_OXTS"
    assert response.alignment_mode == "SOURCE_LOCAL"
    assert response.transform_revision == "kitti-mercator-enu-v1"
    assert response.trajectory.ego_source == "KITTI_OXTS"
    assert response.trajectory.alignment_mode == "SOURCE_LOCAL"
    assert response.trajectory.sample_count == 6
    assert response.route_summary.bounds.min_x <= response.route_summary.bounds.max_x
    assert response.route_summary.bounds.min_y <= response.route_summary.bounds.max_y
    assert manager.get_session(response.simulation_id).current_tick == 0


def test_request_rejects_inconsistent_source_and_alignment() -> None:
    with pytest.raises(ValueError, match="ego_source must be 'KITTI_OXTS'"):
        CreateSimulationRequest(
            dataset_name="kitti",
            sequence_id=SEQUENCE_ID,
            ego_source="SIMULATION",
        )

    with pytest.raises(ValueError, match="alignment_mode must be 'GEOGRAPHIC'"):
        CreateSimulationRequest(
            dataset_name="kitti",
            sequence_id=SEQUENCE_ID,
            scene_id="default",
            alignment_mode="SOURCE_LOCAL",
        )


def test_geographic_alignment_rejects_a_trajectory_outside_the_scene(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    root = _make_kitti_root(tmp_path)
    scene = SimpleNamespace(
        scene_revision="scene-revision",
        coordinates=SimpleNamespace(
            origin=SimpleNamespace(latitude=1.3, longitude=103.8)
        ),
        bounds=SimpleNamespace(min_x=-100, min_y=-100, max_x=100, max_y=100),
    )
    monkeypatch.setattr(
        "src.services.session_manager.get_scene_repository",
        lambda: SimpleNamespace(metadata=lambda _scene_id: scene),
    )

    def stream_factory(**config):
        return build_trajectory_stream(**config, kitti_root=root)

    manager = SessionManager(stream_factory=stream_factory)
    with pytest.raises(InvalidSimulationSourceError, match="does not intersect scene"):
        manager.create(
            CreateSimulationRequest(
                dataset_name="kitti",
                sequence_id=SEQUENCE_ID,
                scene_id="default",
                ego_source="KITTI_OXTS",
                alignment_mode="GEOGRAPHIC",
            )
        )


def test_geographic_kitti_session_exposes_map_match_quality_and_tick_match(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    root = _make_kitti_root(tmp_path)
    scene = SimpleNamespace(
        scene_revision="scene-revision",
        coordinates=SimpleNamespace(
            origin=SimpleNamespace(latitude=49.015, longitude=8.434)
        ),
        bounds=SimpleNamespace(min_x=-1000, min_y=-1000, max_x=1000, max_y=1000),
    )
    road = SimpleNamespace(
        road_id=42,
        highway_type="secondary",
        points=(
            SimpleNamespace(x=-1000.0, y=0.0, z=0.0),
            SimpleNamespace(x=1000.0, y=0.0, z=0.0),
        ),
    )
    repository = SimpleNamespace(
        metadata=lambda _scene_id: scene,
        chunk=lambda *_args, **_kwargs: SimpleNamespace(roads=[road]),
    )
    monkeypatch.setattr(
        "src.services.session_manager.get_scene_repository", lambda: repository
    )

    def stream_factory(**config):
        return build_trajectory_stream(**config, kitti_root=root)

    manager = SessionManager(stream_factory=stream_factory)
    response = manager.create(
        CreateSimulationRequest(
            dataset_name="kitti",
            sequence_id=SEQUENCE_ID,
            scene_id="default",
            alignment_mode="GEOGRAPHIC",
        )
    )

    summary = response.trajectory.map_matching
    assert summary is not None
    assert summary.algorithm == "nearest_road_segment_v1"
    assert summary.road_sequence == [42]
    tick = manager.get_session(response.simulation_id).stream.tick_message(
        response.simulation_id, 0
    )
    assert tick["ego"]["position"] != tick["ego"]["map_match"]["matched_position"]
    assert tick["ego"]["map_match"]["road_id"] == 42


def test_dataset_selection_rejects_missing_or_unsafe_sequences(tmp_path: Path) -> None:
    with pytest.raises(TrajectorySourceError, match="sequence_id is required"):
        build_trajectory_stream(
            dataset_name="kitti",
            sequence_id=None,
            playback_speed=1.0,
            kitti_root=tmp_path,
        )

    with pytest.raises(TrajectorySourceError, match="sequence_id must contain"):
        build_trajectory_stream(
            dataset_name="kitti",
            sequence_id="../outside",
            playback_speed=1.0,
            kitti_root=tmp_path,
        )
