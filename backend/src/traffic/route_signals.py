"""Where a shuttle's route crosses a traffic light's stop line, and whether the light lets it go.

The lights are the simulated ones of signal_cycle: their timings are made up, though their places are
LTA's. A stop line holds traffic going the way its approach heads, so a route crosses one where it runs
that way close to the line. This matches the Unity client's traffic, which reads the same signal file
and the same clock (the session's simulation time), so the shuttle, the cars and the lights drawn agree.
"""

from __future__ import annotations

import json
import math
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path

from src.traffic import signal_cycle

# Where the road-feature importer writes an area's signals (import_road_features.output_path)
SIGNALS_DIRECTORY = Path(__file__).resolve().parents[2] / "fixtures" / "traffic"

# A stop line this close to a leg of the route, running its way, is one the route stops at
MATCH_M = 8.0
ALIGN = 0.7
# Two lines this close along the route are the same line, as for a junction's lanes
APART_M = 10.0
# A shuttle stops for a yellow if this firm a stop will do; closer to the line it carries on through
YELLOW_BRAKE_MPS2 = 6.0
# The shuttle's front stops this far short of the line
STOP_MARGIN_M = 0.5


@dataclass(frozen=True)
class StopLine:
    progress_m: float
    offset_s: int
    phase: int
    # The junction whose light it is, so a fault there (signal_cycle) holds it
    junction_id: str = ""


@lru_cache(maxsize=4)
def junctions(area: str = "toa-payoh", path: Path | None = None) -> tuple[dict, ...]:
    """An area's signalled junctions, or none when its signal data was not imported."""
    file = path or SIGNALS_DIRECTORY / f"{area}-signals.json"
    if not file.is_file():
        return ()
    return tuple(json.loads(file.read_text(encoding="utf-8"))["junctions"])


def stop_lines(points: list[tuple[float, float]], signal_junctions: tuple[dict, ...] | list[dict]) -> list[StopLine]:
    """The stop lines a route crosses going its way, in order along it."""
    lengths = [math.dist(a, b) for a, b in zip(points, points[1:])]
    lines: list[StopLine] = []
    for junction in signal_junctions:
        for approach in junction.get("approaches", []):
            heading = math.radians(approach["heading_deg"])
            travel = (math.cos(heading), math.sin(heading))
            stop = (approach["stop_x_m"], approach["stop_y_m"])
            travelled = 0.0
            for (a, b), length in zip(zip(points, points[1:]), lengths):
                if length > 0:
                    run = ((b[0] - a[0]) / length, (b[1] - a[1]) / length)
                    share = max(0.0, min(length, (stop[0] - a[0]) * run[0] + (stop[1] - a[1]) * run[1]))
                    near = (a[0] + run[0] * share, a[1] + run[1] * share)
                    at = travelled + share
                    if (math.dist(stop, near) <= MATCH_M and run[0] * travel[0] + run[1] * travel[1] >= ALIGN
                            and all(abs(line.progress_m - at) >= APART_M for line in lines)):
                        lines.append(StopLine(at, int(junction["offset_s"]), int(approach["phase"]), str(junction["id"])))
                travelled += length
    return sorted(lines, key=lambda line: line.progress_m)


def must_stop(line: StopLine, time_s: float, front_to_line_m: float, speed_mps: float,
              faults: dict[str, float] | None = None) -> bool:
    """Whether the light tells a vehicle with its front this far short of the line to stop. A faulted
    junction (faults, as signal_cycle takes them) shows red until its fault clears."""
    if front_to_line_m < 0:
        return False
    until = signal_cycle.fault_until(faults, line.junction_id, time_s)
    color = signal_cycle.phase_state(time_s, line.offset_s, line.phase, until)["color"]
    if color == "green":
        return False
    # Too close to stop for a yellow: carry on through it
    return color == "red" or speed_mps * speed_mps / (2 * YELLOW_BRAKE_MPS2) <= front_to_line_m


def hold_point(lines: list[StopLine], time_s: float, progress_m: float, front_m: float, speed_mps: float,
               closed_length_m: float | None = None, faults: dict[str, float] | None = None) -> float | None:
    """The furthest a shuttle at progress_m may go before a light holds it, or None if none does.

    On a closed route the lines of its next lap count too. A faulted junction holds it until it clears.
    """
    laps = (0.0, closed_length_m) if closed_length_m else (0.0,)
    holds = [line.progress_m + lap - front_m - STOP_MARGIN_M
             for lap in laps for line in lines
             if must_stop(line, time_s, line.progress_m + lap - progress_m - front_m, speed_mps, faults)]
    return max(progress_m, min(holds)) if holds else None
