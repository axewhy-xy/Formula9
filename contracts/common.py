"""
Transport-independent data types shared by the REST and WebSocket contracts.

Author: Lau Xiang Yang
Date: 14/09/2026
Last Modified: 14/09/2026

Description:
These classes describe the renderer-independent M1 wire format.  They use only
the Python standard library so the contract package can be imported without the
backend or frontend dependencies.  ``to_dict`` returns JSON-serialisable data;
in particular, optional values are retained as ``None`` so they become JSON
``null`` rather than disappearing from fixture messages.

"""

from dataclasses import asdict, dataclass, field
from typing import Any, Literal


CONTRACT_VERSION = "1.0"


@dataclass(frozen=True, slots=True, kw_only=True)
class ContractModel:
    """Base class for contract values that need JSON serialisation."""

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass(frozen=True, slots=True, kw_only=True)
class GeographicPoint(ContractModel):
    latitude: float
    longitude: float


@dataclass(frozen=True, slots=True, kw_only=True)
class CoordinatesMetadata(ContractModel):
    """Coordinate convention used by every local position in the contract."""

    version: str = "1.0"
    system: Literal["local_cartesian"] = "local_cartesian"
    x_unit: Literal["m"] = "m"
    y_unit: Literal["m"] = "m"
    z_unit: Literal["m"] = "m"
    heading_unit: Literal["deg"] = "deg"
    geographic_crs: Literal["EPSG:4326"] = "EPSG:4326"
    x_axis: Literal["east"] = "east"
    y_axis: Literal["north"] = "north"
    z_axis: Literal["up"] = "up"
    heading_zero_axis: Literal["positive_x"] = "positive_x"
    heading_positive_direction: Literal["counter_clockwise"] = "counter_clockwise"
    origin: GeographicPoint = field(
        default_factory=lambda: GeographicPoint(latitude=1.2998, longitude=103.7874)
    )


@dataclass(frozen=True, slots=True, kw_only=True)
class Position3D(ContractModel):
    """Local Cartesian position in metres: x=east, y=north, z=up."""

    x: float
    y: float
    z: float = 0.0


@dataclass(frozen=True, slots=True, kw_only=True)
class Orientation(ContractModel):
    """Euler angles in degrees using the convention in CoordinatesMetadata."""

    heading: float
    pitch: float = 0.0
    roll: float = 0.0


@dataclass(frozen=True, slots=True, kw_only=True)
class TrajectoryPoint(ContractModel):
    index: int
    simulation_time: float
    position: Position3D
    orientation: Orientation
    speed: float
    speed_unit: Literal["m/s"] = "m/s"
    source_frame: int | None = None
    source_timestamp: str | None = None


@dataclass(frozen=True, slots=True, kw_only=True)
class RoadMatch(ContractModel):
    """Nearest drivable OSM road position for one recorded ego sample."""

    road_id: int
    highway_type: str
    matched_position: Position3D
    cross_track_error: float
    distance_unit: Literal["m"] = "m"


@dataclass(frozen=True, slots=True, kw_only=True)
class MapMatchingSummary(ContractModel):
    """Quality metrics and acceptance targets for one matched trajectory."""

    algorithm: Literal["nearest_road_segment_v1"] = "nearest_road_segment_v1"
    sample_count: int
    matched_sample_count: int
    road_sequence: tuple[int, ...]
    coverage_ratio: float
    mean_cross_track_error: float | None
    p95_cross_track_error: float | None
    max_cross_track_error: float | None
    maximum_match_distance: float
    acceptance_min_coverage_ratio: float
    acceptance_max_mean_cross_track_error: float
    acceptance_max_p95_cross_track_error: float
    acceptance_passed: bool
    distance_unit: Literal["m"] = "m"


@dataclass(frozen=True, slots=True, kw_only=True)
class TrajectoryMetadata(ContractModel):
    """Description of the normalized trajectory owned by a session."""

    sample_count: int
    duration: float
    duration_unit: Literal["s"] = "s"
    dataset_name: str | None = None
    sequence_id: str | None = None
    route_id: str | None = None
    source_coordinate_system: str | None = None
    source_start_timestamp: str | None = None
    source_end_timestamp: str | None = None
    ego_source: str | None = None
    alignment_mode: str | None = None
    scene_id: str | None = None
    scene_revision: str | None = None
    transform_revision: str | None = None
    map_matching: MapMatchingSummary | None = None


@dataclass(frozen=True, slots=True, kw_only=True)
class ErrorDetail(ContractModel):
    error_code: str
    error_message: str
    error_details: dict[str, Any] | None = None
