"""
Server-to-client WebSocket state contract.

Author: Lau Xiang Yang
Date: 14/09/2026
Last Modified: 14/09/2026

Description:
All positions are renderer-independent local Cartesian values. The backend is
authoritative for state; browser and C++ clients consume the same messages.

"""

from dataclasses import dataclass, field
from typing import Any, Literal, TypeAlias

from contracts.common import (
    CONTRACT_VERSION,
    ContractModel,
    CoordinatesMetadata,
    ErrorDetail,
    RoadMatch,
    Orientation,
    Position3D,
    TrajectoryMetadata,
    TrajectoryPoint,
)


@dataclass(frozen=True, slots=True, kw_only=True)
class BackendEnvelope(ContractModel):
    simulation_id: str
    timestamp: str
    coordinates: CoordinatesMetadata = field(default_factory=CoordinatesMetadata)
    contract_version: str = CONTRACT_VERSION


@dataclass(frozen=True, slots=True, kw_only=True)
class SessionReadyMessage(BackendEnvelope):
    message_type: Literal["SESSION_READY"] = field(default="SESSION_READY", init=False)
    status: Literal["READY"] = "READY"
    tick_rate: float = 10.0
    tick_rate_unit: Literal["hz"] = "hz"
    current_tick: int = 0
    simulation_time: float = 0.0
    simulation_time_unit: Literal["s"] = "s"


@dataclass(frozen=True, slots=True, kw_only=True)
class TrajectoryMetadataMessage(BackendEnvelope):
    """Sent after SESSION_READY so clients can size buffers before START."""

    trajectory: TrajectoryMetadata
    message_type: Literal["TRAJECTORY_METADATA"] = field(
        default="TRAJECTORY_METADATA", init=False
    )


@dataclass(frozen=True, slots=True, kw_only=True)
class EgoState(ContractModel):
    position: Position3D
    orientation: Orientation
    speed: float
    source_frame: int
    speed_unit: Literal["m/s"] = "m/s"
    source_timestamp: str | None = None
    map_match: RoadMatch | None = None


@dataclass(frozen=True, slots=True, kw_only=True)
class TrajectoryWindow(ContractModel):
    """Current sample plus a bounded recent/planned window."""

    current_index: int
    points: tuple[TrajectoryPoint, ...]


@dataclass(frozen=True, slots=True, kw_only=True)
class ObstacleState(ContractModel):
    obstacle_id: str
    obstacle_type: str
    position: Position3D
    velocity: Position3D | None = None
    velocity_unit: Literal["m/s"] = "m/s"


@dataclass(frozen=True, slots=True, kw_only=True)
class ObstacleTriggeredMessage(BackendEnvelope):
    tick: int
    simulation_time: float
    obstacle: ObstacleState
    message_type: Literal["OBSTACLE_TRIGGERED"] = field(
        default="OBSTACLE_TRIGGERED", init=False
    )
    simulation_time_unit: Literal["s"] = "s"


@dataclass(frozen=True, slots=True, kw_only=True)
class DecisionState(ContractModel):
    action: str
    reason_code: str
    summary: str
    factors: tuple[dict[str, Any], ...] = field(default_factory=tuple)
    eligible_actions: tuple[str, ...] = field(default_factory=tuple)
    rejected_actions: tuple[dict[str, str], ...] = field(default_factory=tuple)
    conflict: bool = False
    model_scores: dict[str, float] | None = None


@dataclass(frozen=True, slots=True, kw_only=True)
class TickMessage(BackendEnvelope):
    tick: int
    simulation_time: float
    ego: EgoState
    trajectory: TrajectoryWindow
    message_type: Literal["TICK"] = field(default="TICK", init=False)
    simulation_time_unit: Literal["s"] = "s"
    obstacles: tuple[ObstacleState, ...] = field(default_factory=tuple)
    decision: DecisionState | None = None


@dataclass(frozen=True, slots=True, kw_only=True)
class CompletionSummary(ContractModel):
    distance_travelled: float
    decision_count: int
    intervention_count: int
    collision_count: int
    distance_unit: Literal["m"] = "m"


@dataclass(frozen=True, slots=True, kw_only=True)
class SimulationCompleteMessage(BackendEnvelope):
    final_tick: int
    simulation_time: float
    completion_reason: str
    summary: CompletionSummary
    message_type: Literal["SIMULATION_COMPLETE"] = field(
        default="SIMULATION_COMPLETE", init=False
    )
    status: Literal["COMPLETED"] = "COMPLETED"
    simulation_time_unit: Literal["s"] = "s"


@dataclass(frozen=True, slots=True, kw_only=True)
class ErrorMessage(BackendEnvelope):
    error: ErrorDetail
    message_type: Literal["ERROR"] = field(default="ERROR", init=False)


BackendMessage: TypeAlias = (
    SessionReadyMessage
    | TrajectoryMetadataMessage
    | TickMessage
    | ObstacleTriggeredMessage
    | SimulationCompleteMessage
    | ErrorMessage
)


__all__ = [
    "BackendEnvelope",
    "BackendMessage",
    "CompletionSummary",
    "DecisionState",
    "EgoState",
    "ErrorMessage",
    "ObstacleTriggeredMessage",
    "ObstacleState",
    "SessionReadyMessage",
    "SimulationCompleteMessage",
    "TickMessage",
    "TrajectoryMetadataMessage",
    "TrajectoryWindow",
]

