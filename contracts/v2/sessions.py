"""Session admission for versioned curated fleet scenarios."""

from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field

from .models import Id


class CreateSessionRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)

    scenario_revision_id: Id
    seed: Annotated[int, Field(ge=0, le=2147483647)] = 1000


class SessionListEntry(BaseModel):
    session_id: Id
    scenario_revision_id: Id
    status: Literal["DRAFT", "READY", "RUNNING", "PAUSED", "RECOVERING", "COMPLETED", "ARCHIVED", "FAILED"]
    occupies_capacity: bool


class SessionListResponse(BaseModel):
    sessions: list[SessionListEntry]
    live_count: Annotated[int, Field(ge=0)]
    capacity: Annotated[int, Field(ge=1)]

