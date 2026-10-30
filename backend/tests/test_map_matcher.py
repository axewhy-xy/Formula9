"""Deterministic unit tests for the lightweight OSM road matcher."""

from types import SimpleNamespace

from src.routing.map_matcher import match_trajectory_to_roads


def _point(x: float, y: float, z: float = 0.0) -> SimpleNamespace:
    return SimpleNamespace(x=x, y=y, z=z)


def _sample(x: float, y: float) -> SimpleNamespace:
    return SimpleNamespace(position=_point(x, y))


def _road(road_id: int, highway_type: str, *points: SimpleNamespace) -> SimpleNamespace:
    return SimpleNamespace(road_id=road_id, highway_type=highway_type, points=points)


def test_matches_to_nearest_drivable_segment_without_replacing_raw_pose() -> None:
    samples = [_sample(5, 4), _sample(15, 3)]
    roads = [
        _road(10, "secondary", _point(0, 0), _point(20, 0)),
        _road(20, "residential", _point(0, 20), _point(20, 20)),
    ]

    result = match_trajectory_to_roads(samples, roads)

    assert [match.road_id for match in result.matches if match] == [10, 10]
    assert result.matches[0].matched_position.x == 5
    assert result.matches[0].matched_position.y == 0
    assert result.matches[0].cross_track_error == 4
    assert samples[0].position.y == 4
    assert result.summary.road_sequence == (10,)
    assert result.summary.coverage_ratio == 1
    assert result.summary.acceptance_passed is True


def test_excludes_non_drivable_paths_and_reports_failed_quality() -> None:
    result = match_trajectory_to_roads(
        [_sample(0, 0), _sample(100, 100)],
        [_road(30, "footway", _point(-1, 0), _point(1, 0))],
    )

    assert result.matches == (None, None)
    assert result.summary.matched_sample_count == 0
    assert result.summary.road_sequence == ()
    assert result.summary.coverage_ratio == 0
    assert result.summary.mean_cross_track_error is None
    assert result.summary.acceptance_passed is False
