"""Session admission for versioned curated fleet scenarios."""

from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field

from .models import Id

# A name people give a session so they can find it in the list. Display text only: the session ID
# stays the key, so names need not be unique. It starts and ends with a visible character.
SessionName = Annotated[str, Field(min_length=1, max_length=64, pattern=r"^\S(?:.*\S)?$")]


class CreateSessionRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)

    scenario_revision_id: Id
    seed: Annotated[int, Field(ge=0, le=2147483647)] = 1000
    name: SessionName | None = None


class SessionListEntry(BaseModel):
    session_id: Id
    name: SessionName | None = None
    scenario_revision_id: Id
    status: Literal["DRAFT", "READY", "RUNNING", "PAUSED", "RECOVERING", "COMPLETED", "ARCHIVED", "FAILED"]
    occupies_capacity: bool
    is_assessment: bool = False
    can_delete: bool | None = None
    can_archive: bool | None = None


class SessionListResponse(BaseModel):
    sessions: list[SessionListEntry]
    live_count: Annotated[int, Field(ge=0)]
    total: Annotated[int, Field(ge=0)]
    capacity: Annotated[int, Field(ge=1)]
    terminal_practice_count: Annotated[int, Field(ge=0)] | None = None

