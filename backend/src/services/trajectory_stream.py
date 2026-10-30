"""
Trajectory loading, fixed-tick sampling, and wire-message construction.

Author: Lau Xiang Yang, CC
Date Created: 20/09/2026
Last Modified: 20/09/2026

Description:
    This service is responsible for loading trajectory data, sampling it at a fixed rate, and constructing
    the wire messages that will be sent to the client. It is designed to be used in a multi-threaded
    environment and provides thread-safe access to the trajectory data. This service has no FastAPI dependency. 
    It owns the renderer-independent playback data for one simulation session and can be tested without a
    WebSocket connection.
    
"""

from __future__ import annotations

from bisect import bisect_left
from dataclasses import dataclass, field
from datetime import UTC, datetime, timedelta
from math import atan2, ceil, degrees, hypot
from os import getenv
from pathlib import Path
import re

from contracts.common import (
    CoordinatesMetadata,
    ErrorDetail,
    GeographicPoint,
    RoadMatch,
    TrajectoryMetadata,
)
from contracts.websocket.backend.contract import (
    CompletionSummary,
    EgoState,
    ErrorMessage,
    SessionReadyMessage,
    SimulationCompleteMessage,
    TickMessage,
    TrajectoryMetadataMessage,
    TrajectoryWindow,
)
from src.datasets.kitti_loader import KittiParseError, load_trajectory
from src.datasets.trajectory_contract_adapter import (
    to_contract_coordinates,
    to_contract_point,
)
from src.datasets.trajectory_models import CanonicalSample, Orientation, Position
from src.datasets.trajectory_normalizer import normalize_trajectory
from src.schemas.route import RouteResponse


DEFAULT_TICK_RATE_HZ = 10.0
PLANNED_ROUTE_FALLBACK_SPEED_KPH = 30.0
_SAFE_DATASET_PART = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]*$")
_DEMO_ROUTE = (
    (0.0, 0.0),
    (18.0, 0.0),
    (38.0, 8.0),
    (58.0, 24.0),
    (78.0, 32.0),
    (104.0, 32.0),
    (128.0, 46.0),
)


class TrajectorySourceError(ValueError):
    """Raised when a requested dataset cannot safely be resolved or loaded."""


def utc_timestamp() -> str:
    return datetime.now(UTC).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def _distance(samples: tuple[CanonicalSample, ...]) -> float:
    return sum(
        hypot(end.position.x - start.position.x, end.position.y - start.position.y)
        for start, end in zip(samples, samples[1:])
    )


def _demo_samples() -> tuple[CanonicalSample, ...]:
    """Retain the old deterministic route as an explicit offline fallback."""

    sample_count = 80
    start_time = datetime(2026, 1, 1, tzinfo=UTC)
    samples: list[CanonicalSample] = []
    for index in range(sample_count):
        progress = index / (sample_count - 1)
        scaled = progress * (len(_DEMO_ROUTE) - 1)
        segment = min(int(scaled), len(_DEMO_ROUTE) - 2)
        amount = scaled - segment
        start = _DEMO_ROUTE[segment]
        end = _DEMO_ROUTE[segment + 1]
        x = start[0] + (end[0] - start[0]) * amount
        y = start[1] + (end[1] - start[1]) * amount
        contract_heading = degrees(atan2(end[1] - start[1], end[0] - start[0])) % 360.0
        source_time = start_time + timedelta(seconds=index / DEFAULT_TICK_RATE_HZ)
        samples.append(
            CanonicalSample(
                sample_index=index,
                source_frame=index,
                source_timestamp=source_time.isoformat(timespec="milliseconds").replace(
                    "+00:00", "Z"
                ),
                simulation_time=index / DEFAULT_TICK_RATE_HZ,
                position=Position(x=x, y=y, z=0.0),
                # The ingestion model uses north/clockwise headings; its contract
                # adapter converts them to east/counter-clockwise headings.
                orientation=Orientation(
                    heading=(90.0 - contract_heading) % 360.0,
                    pitch=0.0,
                    roll=0.0,
                ),
                speed=7.6,
            )
        )
    return tuple(samples)


def _planned_route_samples(route: RouteResponse) -> tuple[CanonicalSample, ...]:
    """Resample a route preview into deterministic 10 Hz simulation states."""

    points: list[tuple[float, float, float]] = []
    for point in route.polyline:
        candidate = (float(point.x), float(point.y), float(point.z))
        if not points or candidate != points[-1]:
            points.append(candidate)
    if not points:
        raise TrajectorySourceError("The planned route contains no usable points")

    start_time = datetime(2026, 1, 1, tzinfo=UTC)
    if len(points) == 1:
        point = points[0]
        return (
            CanonicalSample(
                sample_index=0,
                source_frame=0,
                source_timestamp=start_time.isoformat(timespec="milliseconds").replace(
                    "+00:00", "Z"
                ),
                simulation_time=0.0,
                position=Position(x=point[0], y=point[1], z=point[2]),
                orientation=Orientation(heading=90.0, pitch=0.0, roll=0.0),
                speed=0.0,
            ),
        )

    segment_lengths = [
        hypot(end[0] - start[0], end[1] - start[1])
        for start, end in zip(points, points[1:])
    ]
    cumulative_distances = [0.0]
    for length in segment_lengths:
        cumulative_distances.append(cumulative_distances[-1] + length)
    total_distance = cumulative_distances[-1]
    if total_distance <= 0.0:
        raise TrajectorySourceError("The planned route has zero geometric length")

    duration = float(route.estimated_duration)
    if duration <= 0.0:
        duration = total_distance / (PLANNED_ROUTE_FALLBACK_SPEED_KPH / 3.6)
    sample_count = max(2, ceil(duration * DEFAULT_TICK_RATE_HZ) + 1)
    speed = total_distance / duration
    samples: list[CanonicalSample] = []

    for index in range(sample_count):
        simulation_time = (
            duration
            if index == sample_count - 1
            else min(index / DEFAULT_TICK_RATE_HZ, duration)
        )
        requested_distance = total_distance * simulation_time / duration
        insertion_index = bisect_left(cumulative_distances, requested_distance)
        segment_index = min(
            max(insertion_index - 1, 0),
            len(segment_lengths) - 1,
        )
        segment_start = points[segment_index]
        segment_end = points[segment_index + 1]
        segment_distance = segment_lengths[segment_index]
        amount = (
            (requested_distance - cumulative_distances[segment_index])
            / segment_distance
        )
        x = segment_start[0] + (segment_end[0] - segment_start[0]) * amount
        y = segment_start[1] + (segment_end[1] - segment_start[1]) * amount
        z = segment_start[2] + (segment_end[2] - segment_start[2]) * amount
        contract_heading = degrees(
            atan2(
                segment_end[1] - segment_start[1],
                segment_end[0] - segment_start[0],
            )
        ) % 360.0
        source_time = start_time + timedelta(seconds=simulation_time)
        samples.append(
            CanonicalSample(
                sample_index=index,
                source_frame=index,
                source_timestamp=source_time.isoformat(timespec="milliseconds").replace(
                    "+00:00", "Z"
                ),
                simulation_time=round(simulation_time, 6),
                position=Position(x=x, y=y, z=z),
                orientation=Orientation(
                    heading=(90.0 - contract_heading) % 360.0,
                    pitch=0.0,
                    roll=0.0,
                ),
                speed=speed,
            )
        )
    return tuple(samples)


def default_kitti_root() -> Path:
    configured = getenv("KITTI_DATA_ROOT")
    if configured:
        return Path(configured).expanduser().resolve()
    return (Path(__file__).resolve().parents[3] / "data" / "kitti").resolve()


def resolve_kitti_oxts_dir(sequence_id: str, root: Path | None = None) -> Path:
    """Resolve downloader-supported KITTI layouts without accepting a path."""

    if not _SAFE_DATASET_PART.fullmatch(sequence_id):
        raise TrajectorySourceError(
            "sequence_id must contain only letters, numbers, '.', '_' or '-'"
        )

    dataset_root = (root or default_kitti_root()).resolve()
    date = "_".join(sequence_id.split("_")[:3])
    candidates = (
        dataset_root / sequence_id / date / f"{sequence_id}_sync" / "oxts" / "data",
        dataset_root / sequence_id / f"{sequence_id}_sync" / "oxts" / "data",
        dataset_root / sequence_id / "oxts" / "data",
    )
    for candidate in candidates:
        resolved = candidate.resolve()
        if dataset_root not in resolved.parents:
            continue
        if resolved.is_dir():
            return resolved
    raise TrajectorySourceError(
        f"KITTI sequence '{sequence_id}' was not found below {dataset_root}. "
        "Run scripts/download_kitti.py or set KITTI_DATA_ROOT."
    )


@dataclass(frozen=True, slots=True)
class TrajectoryStream:
    samples: tuple[CanonicalSample, ...]
    coordinates: CoordinatesMetadata
    metadata: TrajectoryMetadata
    playback_speed: float = 1.0
    tick_rate_hz: float = DEFAULT_TICK_RATE_HZ
    history_points: int = 10
    planned_points: int = 20
    road_matches: tuple[RoadMatch | None, ...] = ()
    _sample_times: tuple[float, ...] = field(init=False, repr=False)

    def __post_init__(self) -> None:
        if not self.samples:
            raise ValueError("A trajectory stream requires at least one sample")
        if self.playback_speed <= 0.0:
            raise ValueError("playback_speed must be greater than zero")
        if self.tick_rate_hz <= 0.0:
            raise ValueError("tick_rate_hz must be greater than zero")
        if self.road_matches and len(self.road_matches) != len(self.samples):
            raise ValueError("road_matches must be empty or align one-to-one with samples")
        sample_times = tuple(sample.simulation_time for sample in self.samples)
        if any(end < start for start, end in zip(sample_times, sample_times[1:])):
            raise ValueError("Trajectory sample times must be monotonically increasing")
        object.__setattr__(self, "_sample_times", sample_times)

    @property
    def duration(self) -> float:
        return self.samples[-1].simulation_time

    @property
    def tick_count(self) -> int:
        return max(1, ceil(self.duration * self.tick_rate_hz) + 1)

    @property
    def tick_interval(self) -> float:
        return 1.0 / (self.tick_rate_hz * self.playback_speed)

    @property
    def route_distance(self) -> float:
        return _distance(self.samples)

    def sample_for_tick(self, tick: int) -> CanonicalSample:
        if tick < 0 or tick >= self.tick_count:
            raise IndexError(f"tick {tick} is outside this trajectory")
        requested_time = min(tick / self.tick_rate_hz, self.duration)
        insertion_index = bisect_left(self._sample_times, requested_time)
        if insertion_index == 0:
            return self.samples[0]
        if insertion_index == len(self.samples):
            return self.samples[-1]

        before_index = insertion_index - 1
        before_distance = requested_time - self._sample_times[before_index]
        after_distance = self._sample_times[insertion_index] - requested_time
        nearest_index = (
            before_index if before_distance <= after_distance else insertion_index
        )
        return self.samples[nearest_index]

    def ready_message(self, simulation_id: str, current_tick: int = 0) -> dict:
        simulation_time = min(current_tick / self.tick_rate_hz, self.duration)
        return SessionReadyMessage(
            simulation_id=simulation_id,
            timestamp=utc_timestamp(),
            coordinates=self.coordinates,
            tick_rate=self.tick_rate_hz,
            current_tick=current_tick,
            simulation_time=simulation_time,
        ).to_dict()

    def metadata_message(self, simulation_id: str) -> dict:
        return TrajectoryMetadataMessage(
            simulation_id=simulation_id,
            timestamp=utc_timestamp(),
            coordinates=self.coordinates,
            trajectory=self.metadata,
        ).to_dict()

    def tick_message(self, simulation_id: str, tick: int) -> dict:
        sample = self.sample_for_tick(tick)
        point = to_contract_point(sample)
        start = max(0, sample.sample_index - self.history_points)
        stop = min(len(self.samples), sample.sample_index + self.planned_points + 1)
        window = tuple(to_contract_point(item) for item in self.samples[start:stop])
        road_match = (
            self.road_matches[sample.sample_index] if self.road_matches else None
        )
        return TickMessage(
            simulation_id=simulation_id,
            timestamp=utc_timestamp(),
            coordinates=self.coordinates,
            tick=tick,
            simulation_time=round(min(tick / self.tick_rate_hz, self.duration), 6),
            ego=EgoState(
                position=point.position,
                orientation=point.orientation,
                speed=point.speed,
                speed_unit=point.speed_unit,
                source_frame=sample.source_frame,
                source_timestamp=sample.source_timestamp,
                map_match=road_match,
            ),
            trajectory=TrajectoryWindow(current_index=sample.sample_index, points=window),
        ).to_dict()

    def complete_message(self, simulation_id: str, final_tick: int) -> dict:
        return SimulationCompleteMessage(
            simulation_id=simulation_id,
            timestamp=utc_timestamp(),
            coordinates=self.coordinates,
            final_tick=final_tick,
            simulation_time=self.duration,
            completion_reason="TRAJECTORY_COMPLETE",
            summary=CompletionSummary(
                distance_travelled=round(self.route_distance, 3),
                decision_count=0,
                intervention_count=0,
                collision_count=0,
            ),
        ).to_dict()

    def error_message(
        self,
        simulation_id: str,
        error_code: str,
        error_message: str,
        error_details: dict | None = None,
    ) -> dict:
        return ErrorMessage(
            simulation_id=simulation_id,
            timestamp=utc_timestamp(),
            coordinates=self.coordinates,
            error=ErrorDetail(
                error_code=error_code,
                error_message=error_message,
                error_details=error_details,
            ),
        ).to_dict()


def build_trajectory_stream(
    *,
    dataset_name: str | None,
    sequence_id: str | None,
    playback_speed: float,
    kitti_root: Path | None = None,
    coordinate_origin: GeographicPoint | None = None,
) -> TrajectoryStream:
    """Load a requested trajectory, or build the deterministic demo fallback."""

    if dataset_name is None and sequence_id is None:
        samples = _demo_samples()
        coordinates = CoordinatesMetadata(
            origin=coordinate_origin or GeographicPoint(latitude=1.2998, longitude=103.7874)
        )
        metadata = TrajectoryMetadata(
            sample_count=len(samples),
            duration=samples[-1].simulation_time,
            dataset_name="demo",
            sequence_id="deterministic_route",
            source_coordinate_system="synthetic_local_cartesian",
            source_start_timestamp=samples[0].source_timestamp,
            source_end_timestamp=samples[-1].source_timestamp,
        )
        return TrajectoryStream(samples, coordinates, metadata, playback_speed)

    resolved_dataset = (dataset_name or "kitti").lower()
    if resolved_dataset != "kitti":
        raise TrajectorySourceError(
            f"Unsupported dataset_name '{dataset_name}'. Supported value: 'kitti'."
        )
    if not sequence_id:
        raise TrajectorySourceError("sequence_id is required when dataset_name is 'kitti'")

    data_dir = resolve_kitti_oxts_dir(sequence_id, kitti_root)
    try:
        trajectory = load_trajectory(data_dir, sequence_id=sequence_id)
        normalization_origin = (
            None
            if coordinate_origin is None
            else (coordinate_origin.latitude, coordinate_origin.longitude)
        )
        normalized = tuple(
            normalize_trajectory(
                trajectory,
                data_dir,
                coordinate_origin=normalization_origin,
            )
        )
    except KittiParseError as error:
        raise TrajectorySourceError(str(error)) from error

    coordinates = (
        CoordinatesMetadata(origin=coordinate_origin)
        if coordinate_origin is not None
        else to_contract_coordinates(trajectory)
    )
    metadata = TrajectoryMetadata(
        sample_count=len(normalized),
        duration=normalized[-1].simulation_time,
        dataset_name="kitti",
        sequence_id=sequence_id,
        source_coordinate_system="KITTI_OXTS_WGS84",
        source_start_timestamp=normalized[0].source_timestamp,
        source_end_timestamp=normalized[-1].source_timestamp,
    )
    return TrajectoryStream(normalized, coordinates, metadata, playback_speed)


def build_planned_route_stream(
    *,
    route: RouteResponse,
    playback_speed: float,
    coordinate_origin: GeographicPoint,
) -> TrajectoryStream:
    """Turn a stored route preview into the authoritative simulation stream."""

    samples = _planned_route_samples(route)
    coordinates = CoordinatesMetadata(origin=coordinate_origin)
    metadata = TrajectoryMetadata(
        sample_count=len(samples),
        duration=samples[-1].simulation_time,
        route_id=route.route_id,
        source_coordinate_system="planned_route_local_cartesian",
        source_start_timestamp=samples[0].source_timestamp,
        source_end_timestamp=samples[-1].source_timestamp,
    )
    return TrajectoryStream(samples, coordinates, metadata, playback_speed)


__all__ = [
    "DEFAULT_TICK_RATE_HZ",
    "TrajectorySourceError",
    "TrajectoryStream",
    "build_planned_route_stream",
    "build_trajectory_stream",
    "default_kitti_root",
    "resolve_kitti_oxts_dir",
]
