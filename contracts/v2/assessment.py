"""Wire contract for an authenticated trainee's assessment.

Author: Nur Syazliyarni Binte Zazali
Date Created: 15/11/2026
Last Modified: 23/11/2026
"""

from typing import Annotated, Literal

from pydantic import Field, FiniteFloat, NonNegativeInt

from .models import Id, WireModel


class AssessmentCriterion(WireModel):
    """Points and the durable simulation evidence supporting a single rule."""

    rule_id: Id
    label: str
    points: FiniteFloat | None
    maximum: NonNegativeInt
    explanation: str
    event_ids: list[Id]


class AssessmentFailure(WireModel):
    """A confirmed safety breach that overrides the numerical grade."""

    rule_id: Id
    explanation: str
    event_ids: list[Id]


class SessionAssessment(WireModel):
    """Current status or the persisted final report for one session."""

    session_id: Id
    scenario_revision_id: Id
    rubric_revision: Id | None
    duration_s: Annotated[int, Field(gt=0)] | None = None
    status: Literal["PRACTICE", "IN_PROGRESS", "INCOMPLETE", "PASS", "FAIL"]
    points: FiniteFloat | None
    maximum: NonNegativeInt | None
    pass_mark: NonNegativeInt | None
    through_tick: NonNegativeInt | None
    through_event_sequence: NonNegativeInt | None
    explanation: str
    criteria: list[AssessmentCriterion] = Field(default_factory=list)
    automatic_failures: list[AssessmentFailure] = Field(default_factory=list)
