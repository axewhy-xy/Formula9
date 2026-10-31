"""The red, yellow and green a junction's lights show at a given time.

These timings are made up. LTA publishes where each signal head is, but its signal timings are only
available on request, so every junction runs this same plain two-phase cycle. They are not the real
colours at that junction, and should not be presented as if they were.
"""

from __future__ import annotations

import hashlib

GREEN_S = 30
YELLOW_S = 3
ALL_RED_S = 2
PHASES = 2
PHASE_S = GREEN_S + YELLOW_S + ALL_RED_S
CYCLE_S = PHASES * PHASE_S


def junction_offset(junction_id: str) -> int:
    """Seconds into its cycle a junction is at time 0, so junctions do not all change together."""
    return int(hashlib.sha256(junction_id.encode()).hexdigest()[:8], 16) % CYCLE_S


def phase_state(time_s: float, offset_s: int, phase: int) -> dict:
    """A phase's colour at a time, and how many seconds that colour has left."""
    into_phase = (time_s + offset_s - phase * PHASE_S) % CYCLE_S
    if into_phase < GREEN_S:
        return {"phase": phase, "color": "green", "seconds_left": round(GREEN_S - into_phase, 3)}
    if into_phase < GREEN_S + YELLOW_S:
        return {"phase": phase, "color": "yellow", "seconds_left": round(GREEN_S + YELLOW_S - into_phase, 3)}
    return {"phase": phase, "color": "red", "seconds_left": round(CYCLE_S - into_phase, 3)}


def junction_states(junctions: list[dict], time_s: float) -> list[dict]:
    """Every junction's phases at a time."""
    return [{"id": junction["id"], "phases": [phase_state(time_s, junction["offset_s"], phase) for phase in range(PHASES)]}
            for junction in junctions]
