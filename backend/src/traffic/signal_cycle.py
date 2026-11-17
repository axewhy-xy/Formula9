"""The red, yellow and green a junction's lights show at a given time.

These timings are made up. LTA publishes where each signal head is, but its signal timings are only
available on request, so every junction runs this same plain two-phase cycle. They are not the real
colours at that junction, and should not be presented as if they were.

A junction can also be faulted (a SIGNAL_FAULT world event): its lights are stuck on red on every
approach until the fault clears. Faults are passed in as {junction id: simulation second it clears},
and every reader of the lights (the background traffic, a shuttle's stop lines and the signal state the
3D view draws) takes them from here, so all three agree.
"""

from __future__ import annotations

import hashlib

GREEN_S = 30
YELLOW_S = 3
ALL_RED_S = 2
PHASES = 2
PHASE_S = GREEN_S + YELLOW_S + ALL_RED_S
CYCLE_S = PHASES * PHASE_S
# What a faulted junction shows: red on every approach, so nothing may enter it
FAULT_MODE = "STUCK_RED"


def junction_offset(junction_id: str) -> int:
    """Seconds into its cycle a junction is at time 0, so junctions do not all change together."""
    return int(hashlib.sha256(junction_id.encode()).hexdigest()[:8], 16) % CYCLE_S


def fault_until(faults: dict[str, float] | None, junction_id: str | None, time_s: float) -> float | None:
    """When a junction's fault clears, if it is faulted at time_s; else None."""
    if not faults or junction_id is None:
        return None
    until = faults.get(junction_id)
    return until if until is not None and time_s < until else None


def phase_state(time_s: float, offset_s: int, phase: int, fault_until_s: float | None = None) -> dict:
    """A phase's colour at a time, and how many seconds that colour has left. A junction faulted until
    fault_until_s shows red until then."""
    if fault_until_s is not None and time_s < fault_until_s:
        return {"phase": phase, "color": "red", "seconds_left": round(fault_until_s - time_s, 3)}
    into_phase = (time_s + offset_s - phase * PHASE_S) % CYCLE_S
    if into_phase < GREEN_S:
        return {"phase": phase, "color": "green", "seconds_left": round(GREEN_S - into_phase, 3)}
    if into_phase < GREEN_S + YELLOW_S:
        return {"phase": phase, "color": "yellow", "seconds_left": round(GREEN_S + YELLOW_S - into_phase, 3)}
    return {"phase": phase, "color": "red", "seconds_left": round(CYCLE_S - into_phase, 3)}


def junction_states(junctions: list[dict], time_s: float, faults: dict[str, float] | None = None) -> list[dict]:
    """Every junction's phases at a time. A faulted junction says so, and when its fault clears."""
    states = []
    for junction in junctions:
        until = fault_until(faults, junction["id"], time_s)
        state = {"id": junction["id"],
                 "phases": [phase_state(time_s, junction["offset_s"], phase, until) for phase in range(PHASES)]}
        if until is not None:
            state["fault"] = {"mode": FAULT_MODE, "until_s": round(until, 3)}
        states.append(state)
    return states
