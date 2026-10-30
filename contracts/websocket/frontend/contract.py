"""
Client-to-server WebSocket control contract.

Author: Lau Xiang Yang
Date: 14/09/2026
Last Modified: 14/09/2026

Description:
The connection URL is ``/ws/simulations/{simulation_id}``.  Controls still
carry the simulation identifier to make recorded messages self-describing and
to let the server reject messages sent to the wrong session.

"""

from dataclasses import dataclass, field
from typing import Literal, TypeAlias

from contracts.common import (
    CONTRACT_VERSION,
    ContractModel,
    CoordinatesMetadata,
    Position3D,
)


@dataclass(frozen=True, slots=True, kw_only=True)
class ControlEnvelope(ContractModel):
    simulation_id: str
    timestamp: str
    coordinates: CoordinatesMetadata = field(default_factory=CoordinatesMetadata)
    contract_version: str = CONTRACT_VERSION


@dataclass(frozen=True, slots=True, kw_only=True)
class StartControl(ControlEnvelope):
    message_type: Literal["START"] = field(default="START", init=False)
    playback_speed: float = 1.0


@dataclass(frozen=True, slots=True, kw_only=True)
class PauseControl(ControlEnvelope):
    message_type: Literal["PAUSE"] = field(default="PAUSE", init=False)


@dataclass(frozen=True, slots=True, kw_only=True)
class ResumeControl(ControlEnvelope):
    message_type: Literal["RESUME"] = field(default="RESUME", init=False)


@dataclass(frozen=True, slots=True, kw_only=True)
class StopControl(ControlEnvelope):
    message_type: Literal["STOP"] = field(default="STOP", init=False)
    reason: str = "USER_REQUESTED"


@dataclass(frozen=True, slots=True, kw_only=True)
class RuntimeObstacle(ContractModel):
    obstacle_id: str
    obstacle_type: str
    position: Position3D
    velocity: Position3D | None = None
    velocity_unit: Literal["m/s"] = "m/s"


@dataclass(frozen=True, slots=True, kw_only=True)
class TriggerObstacleControl(ControlEnvelope):
    message_type: Literal["TRIGGER_OBSTACLE"] = field(
        default="TRIGGER_OBSTACLE", init=False
    )
    obstacle: RuntimeObstacle


FrontendMessage: TypeAlias = (
    StartControl
    | PauseControl
    | ResumeControl
    | StopControl
    | TriggerObstacleControl
)


__all__ = [
    "ControlEnvelope",
    "FrontendMessage",
    "PauseControl",
    "ResumeControl",
    "RuntimeObstacle",
    "StartControl",
    "StopControl",
    "TriggerObstacleControl",
]
