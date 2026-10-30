"""
REST lifecycle contract.

Author: Lau Xiang Yang
Date: 14/09/2026
Last Modified: 14/09/2026

Description:
Endpoints:
    POST   /api/routes            -> RouteResponse (200)
    POST   /api/simulations       -> CreateSimulationResponse (201)
    GET    /api/simulations/{id}  -> SimulationResponse (200)
    DELETE /api/simulations/{id}  -> no response body (204)

This module is a transport contract, not a backend implementation model.  The
same JSON field names are intended for browser and C++ consumers.

"""

from dataclasses import dataclass, field
from typing import Literal

from contracts.common import (
    CONTRACT_VERSION,
    ContractModel,
    CoordinatesMetadata,
    ErrorDetail,
    GeographicPoint,
    Position3D,
    TrajectoryMetadata,
)


SimulationStatus = Literal["READY", "RUNNING", "PAUSED", "COMPLETED", "STOPPED"]


@dataclass(frozen=True, slots=True, kw_only=True)
class InitialObstacle(ContractModel):
    """Optional scenario obstacle expressed in canonical local coordinates."""

    obstacle_id: str
    obstacle_type: str
    position: Position3D
    velocity: Position3D | None = None
    velocity_unit: Literal["m/s"] = "m/s"
    activation_time: float | None = None
    activation_time_unit: Literal["s"] = "s"


@dataclass(frozen=True, slots=True, kw_only=True)
class SimulationRequest(ContractModel):
    """Body accepted by ``POST /api/simulations``.

    ``dataset_name`` and ``sequence_id`` select a KITTI-derived trajectory.
    ``route_id`` accepts a preview previously returned by ``POST /api/routes``.
    When both are null, ``scenario_name`` may select the deterministic fallback.
    """

    contract_version: str = CONTRACT_VERSION
    scenario_name: str = "default"
    dataset_name: str | None = None
    sequence_id: str | None = None
    route_id: str | None = None
    playback_speed: float = 1.0
    coordinates: CoordinatesMetadata = field(default_factory=CoordinatesMetadata)
    origin: GeographicPoint | None = None
    destination: GeographicPoint | None = None
    obstacles: tuple[InitialObstacle, ...] = field(default_factory=tuple)
    decision_mode: Literal["RULE_BASED", "RL_ASSISTED"] = "RULE_BASED"
    scenario_seed: int | None = None


@dataclass(frozen=True, slots=True, kw_only=True)
class RouteBounds(ContractModel):
    """Planar bounds of the normalized trajectory in canonical metres."""

    min_x: float
    min_y: float
    max_x: float
    max_y: float


@dataclass(frozen=True, slots=True, kw_only=True)
class RouteSummary(ContractModel):
    distance: float
    estimated_duration: float
    route_point_count: int
    bounds: RouteBounds
    distance_unit: Literal["m"] = "m"
    duration_unit: Literal["s"] = "s"


@dataclass(frozen=True, slots=True, kw_only=True)
class RouteRequest(ContractModel):
    """Body accepted by ``POST /api/routes`` in canonical local metres."""

    scene_revision: str
    start: Position3D
    destination: Position3D
    contract_version: str = CONTRACT_VERSION
    scene_id: str = "default"
    cost: Literal["distance"] = "distance"


@dataclass(frozen=True, slots=True, kw_only=True)
class SnappedRoutePoint(Position3D):
    node_id: int
    snapping_distance: float


@dataclass(frozen=True, slots=True, kw_only=True)
class RouteResponse(ContractModel):
    """A route preview; creating one does not create a simulation session."""

    route_id: str
    scene_id: str
    scene_revision: str
    transform_revision: str
    routing_mode: Literal["directed", "undirected_prototype"]
    snapped_start: SnappedRoutePoint
    snapped_destination: SnappedRoutePoint
    polyline: tuple[Position3D, ...]
    distance: float
    estimated_duration: float
    bounds: RouteBounds
    warnings: tuple[str, ...] = field(default_factory=tuple)
    contract_version: str = CONTRACT_VERSION
    cost: Literal["distance"] = "distance"
    distance_unit: Literal["m"] = "m"
    duration_unit: Literal["s"] = "s"


@dataclass(frozen=True, slots=True, kw_only=True)
class SimulationResponse(ContractModel):
    """Body returned when a session is created or retrieved."""

    simulation_id: str
    scenario_name: str
    status: SimulationStatus
    created_at: str
    websocket_url: str
    route_summary: RouteSummary
    trajectory: TrajectoryMetadata
    route_id: str | None = None
    coordinates: CoordinatesMetadata = field(default_factory=CoordinatesMetadata)
    contract_version: str = CONTRACT_VERSION


CreateSimulationResponse = SimulationResponse


@dataclass(frozen=True, slots=True, kw_only=True)
class SimulationError(ContractModel):
    """Common REST error body for 4xx and 5xx responses."""

    timestamp: str
    error: ErrorDetail
    coordinates: CoordinatesMetadata = field(default_factory=CoordinatesMetadata)
    contract_version: str = CONTRACT_VERSION


# Compatibility with the original stub names.  New code should use the
# PascalCase names above.
simulation_request = SimulationRequest
simulation_response = SimulationResponse
simulation_error = SimulationError


__all__ = [
    "CreateSimulationResponse",
    "InitialObstacle",
    "RouteBounds",
    "RouteRequest",
    "RouteResponse",
    "RouteSummary",
    "SnappedRoutePoint",
    "SimulationError",
    "SimulationRequest",
    "SimulationResponse",
    "SimulationStatus",
    "simulation_error",
    "simulation_request",
    "simulation_response",
]
