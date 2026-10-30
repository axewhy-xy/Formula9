"""Lightweight nearest-road map matching for geographically aligned trajectories.

The raw KITTI pose remains authoritative. A match is supplemental debug and
inspection data; it never replaces the recorded ego position.
"""

from __future__ import annotations

from dataclasses import dataclass
from math import ceil, hypot
from typing import Protocol, Sequence

from contracts.common import MapMatchingSummary, Position3D, RoadMatch


MAXIMUM_MATCH_DISTANCE_METRES = 15.0
MINIMUM_ACCEPTED_COVERAGE_RATIO = 0.95
MAXIMUM_ACCEPTED_MEAN_ERROR_METRES = 8.0
MAXIMUM_ACCEPTED_P95_ERROR_METRES = 12.0

_NON_DRIVABLE_HIGHWAY_TYPES = frozenset(
    {"bridleway", "cycleway", "footway", "path", "pedestrian", "steps"}
)


class PointLike(Protocol):
    x: float
    y: float
    z: float


class SampleLike(Protocol):
    position: PointLike


class RoadLike(Protocol):
    road_id: int
    highway_type: str
    points: Sequence[PointLike]


@dataclass(frozen=True, slots=True)
class MapMatchingResult:
    matches: tuple[RoadMatch | None, ...]
    summary: MapMatchingSummary


@dataclass(frozen=True, slots=True)
class _RoadSegment:
    road_id: int
    highway_type: str
    start: PointLike
    end: PointLike


def _segments(roads: Sequence[RoadLike]) -> tuple[_RoadSegment, ...]:
    return tuple(
        _RoadSegment(road.road_id, road.highway_type, start, end)
        for road in roads
        if road.highway_type not in _NON_DRIVABLE_HIGHWAY_TYPES
        for start, end in zip(road.points, road.points[1:])
        if start.x != end.x or start.y != end.y
    )


def _nearest_match(
    point: PointLike,
    segments: Sequence[_RoadSegment],
    maximum_distance: float,
) -> RoadMatch | None:
    best: tuple[float, int, str, float, float, float] | None = None
    for segment in segments:
        dx = segment.end.x - segment.start.x
        dy = segment.end.y - segment.start.y
        length_squared = dx * dx + dy * dy
        amount = max(
            0.0,
            min(
                1.0,
                (
                    (point.x - segment.start.x) * dx
                    + (point.y - segment.start.y) * dy
                )
                / length_squared,
            ),
        )
        matched_x = segment.start.x + amount * dx
        matched_y = segment.start.y + amount * dy
        matched_z = segment.start.z + amount * (segment.end.z - segment.start.z)
        distance = hypot(point.x - matched_x, point.y - matched_y)
        candidate = (
            distance,
            segment.road_id,
            segment.highway_type,
            matched_x,
            matched_y,
            matched_z,
        )
        if best is None or candidate[:2] < best[:2]:
            best = candidate

    if best is None or best[0] > maximum_distance:
        return None
    return RoadMatch(
        road_id=best[1],
        highway_type=best[2],
        matched_position=Position3D(x=best[3], y=best[4], z=best[5]),
        cross_track_error=round(best[0], 3),
    )


def _percentile_95(values: Sequence[float]) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    return ordered[max(0, ceil(0.95 * len(ordered)) - 1)]


def match_trajectory_to_roads(
    samples: Sequence[SampleLike],
    roads: Sequence[RoadLike],
    *,
    maximum_distance: float = MAXIMUM_MATCH_DISTANCE_METRES,
) -> MapMatchingResult:
    """Match every sample to its nearest drivable road segment.

    This intentionally implements a deterministic geometric baseline rather
    than claiming lane-level or probabilistic map matching.
    """

    if maximum_distance <= 0:
        raise ValueError("maximum_distance must be greater than zero")
    road_segments = _segments(roads)
    matches = tuple(
        _nearest_match(sample.position, road_segments, maximum_distance)
        for sample in samples
    )
    errors = [
        match.cross_track_error for match in matches if match is not None
    ]
    sample_count = len(samples)
    matched_count = len(errors)
    coverage = matched_count / sample_count if sample_count else 0.0
    mean_error = sum(errors) / matched_count if matched_count else None
    p95_error = _percentile_95(errors)
    maximum_error = max(errors) if errors else None
    road_sequence: list[int] = []
    for match in matches:
        if match is not None and (
            not road_sequence or road_sequence[-1] != match.road_id
        ):
            road_sequence.append(match.road_id)
    accepted = (
        coverage >= MINIMUM_ACCEPTED_COVERAGE_RATIO
        and mean_error is not None
        and mean_error <= MAXIMUM_ACCEPTED_MEAN_ERROR_METRES
        and p95_error is not None
        and p95_error <= MAXIMUM_ACCEPTED_P95_ERROR_METRES
    )
    summary = MapMatchingSummary(
        sample_count=sample_count,
        matched_sample_count=matched_count,
        road_sequence=tuple(road_sequence),
        coverage_ratio=round(coverage, 6),
        mean_cross_track_error=None if mean_error is None else round(mean_error, 3),
        p95_cross_track_error=None if p95_error is None else round(p95_error, 3),
        max_cross_track_error=None if maximum_error is None else round(maximum_error, 3),
        maximum_match_distance=maximum_distance,
        acceptance_min_coverage_ratio=MINIMUM_ACCEPTED_COVERAGE_RATIO,
        acceptance_max_mean_cross_track_error=MAXIMUM_ACCEPTED_MEAN_ERROR_METRES,
        acceptance_max_p95_cross_track_error=MAXIMUM_ACCEPTED_P95_ERROR_METRES,
        acceptance_passed=accepted,
    )
    return MapMatchingResult(matches=matches, summary=summary)


__all__ = [
    "MAXIMUM_MATCH_DISTANCE_METRES",
    "MapMatchingResult",
    "match_trajectory_to_roads",
]
