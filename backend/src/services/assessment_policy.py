"""Operational access and inactivity policy for scored attempts.

Author: Nur Syazliyarni Binte Zazali
Date Created: 23/11/2026
Last Modified: 23/11/2026

Supervisors are configured by the server using authenticated account names.
These operational settings are separate from each attempt's pinned rubric.
"""

from dataclasses import dataclass

from src.core.config import get_settings


@dataclass(frozen=True)
class AssessmentPolicy:
    supervisors: frozenset[str] = frozenset()
    inactivity_timeout_s: int = 900

    def is_supervisor(self, actor_id: str | None) -> bool:
        return actor_id is not None and actor_id in self.supervisors


def configured_policy() -> AssessmentPolicy:
    settings = get_settings()
    return AssessmentPolicy(frozenset(settings.assessment_supervisors), settings.assessment_inactivity_timeout_s)
