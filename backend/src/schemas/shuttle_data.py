"""Private fleet controller state and the canonical v2 presentation boundary."""

from enum import Enum
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, FiniteFloat

from contracts.v2.models import FullSnapshot


class ShuttleState(str, Enum):
    ROUTE_LOOP = "route loop"
    STOP_DWELL = "stop dwell"
    DEPOT_STANDBY = "depot/standby"
    BLOCKED = "blocked/awaiting decision"
    CHARGING = "charging"


class CommandType(str, Enum):
    PAUSE = "pause"
    RESUME = "resume"
    RESET = "reset"


class Command(BaseModel):
    """A control scheduled on the monotonic command clock, including pauses."""

    model_config = ConfigDict(frozen=True)
    type: CommandType
    triggered_tick: int = Field(ge=0)


class CommandOutcome(BaseModel):
    """The command-clock boundary at which a control was actually processed."""

    model_config = ConfigDict(frozen=True)
    processing_tick: int
    command_type: CommandType
    message: str


class Vector2D(BaseModel):
    x: FiniteFloat
    y: FiniteFloat


class Shuttle(BaseModel):
    model_config = ConfigDict(validate_assignment=True)

    ID: int = Field(ge=0)
    route_ID: int | str
    state: ShuttleState
    progress: FiniteFloat = Field(ge=0)
    curr_vel: FiniteFloat = Field(ge=0)
    cruise_vel: FiniteFloat = Field(default=20.0, ge=0)
    battery: FiniteFloat = Field(ge=0, le=100)  # Private percentage; wire units are kWh.
    dwell_timer: FiniteFloat = Field(ge=0)
    position: Vector2D
    direction: Literal[-1, 1] = 1  # Open routes reverse after endpoint dwell.


class FleetSnapshot(FullSnapshot):
    """A detached v2 full snapshot with an additive deterministic actor hash.

    Like other v2 models this is editable by a consumer, but owns no mutable
    controller data. Consumers must never feed edits back into the actor.
    """

    state_hash: str
