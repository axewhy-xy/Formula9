"""
Tests for the provisional battery decision score (backend/src/energy/assessment.py).

Author: Choo Jian Wei
Date: 26/10/2026
Last Modified: 26/10/2026

Description:
    Part of BE-M2-05 (battery decision). Checks each criterion of the
    m2-battery-provisional-r2 rubric at its edges (reserve boundary,
    response-time window, loops target, rejected attempts), that the result is marked
    provisional and uncalibrated, and that the low-reserve fixture's
    CHARGE_NOW decision scores full marks.

"""

import json
from pathlib import Path

import pytest

from backend.src.energy.assessment import (
    ASSUMPTIONS,
    FULL_TIME_CREDIT_S,
    RUBRIC,
    RUBRIC_REVISION,
    TARGET_LOOPS,
    ZERO_TIME_CREDIT_S,
    score_charge_decision,
)
from backend.src.energy.model import ENERGY_R1
from contracts.v2.models import FullSnapshot

FIXTURES = Path(__file__).resolve().parents[2] / "contracts" / "v2" / "fixtures"


def low_reserve():
    snapshot = FullSnapshot.model_validate(
        json.loads((FIXTURES / "low_reserve_snapshot.json").read_text(encoding="utf-8")))
    vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == "shuttle-05")
    option = next(o for o in snapshot.charge_options if o.action == "CHARGE_NOW")
    return vehicle, option


def score(battery_kwh=None, loops_after="keep", response_time_s=3.0, rejected_attempts=0):
    vehicle, option = low_reserve()
    if battery_kwh is not None:
        vehicle = vehicle.model_copy(update={"battery_kwh": battery_kwh})
    if loops_after != "keep":
        option = option.model_copy(update={"loops_after": loops_after})
    return score_charge_decision(ENERGY_R1, vehicle, option, response_time_s, rejected_attempts)


"""
# -----------------------------------------------------------------------------------
# Function: test_fixture_decision_scores_full_marks
# -----------------------------------------------------------------------------------
Description:
    shuttle-05 (1 kWh above reserve) choosing CHARGE_NOW (42 loops) within
    the full-credit window earns every point, and the result carries the
    provisional rubric metadata.

"""
def test_fixture_decision_scores_full_marks():
    result = score()

    assert result["criteria"] == {"reserve_protection": 35, "response_time": 25.0, "energy_outcome": 25.0,
                                  "attempts": 15}
    assert result["total"] == 100.0
    assert result["maximum_points"] == RUBRIC
    assert sum(RUBRIC.values()) == 100
    assert (result["rubric_revision"], result["scope"]) == (RUBRIC_REVISION, "BATTERY_DECISION")
    assert result["provisional"] is True and result["uncalibrated"] is True
    assert result["assumptions"] == ASSUMPTIONS


"""
# -----------------------------------------------------------------------------------
# Function: test_reserve_protection_boundary
# -----------------------------------------------------------------------------------
Description:
    Deciding exactly at reserve (margin 0) still protects it; any amount
    below reserve scores 0. ENERGY_R1 keeps 6 kWh of 40 in reserve.

"""
@pytest.mark.parametrize(("battery_kwh", "points"), [(7.0, 35), (6.0, 35), (5.99, 0), (0.0, 0)],
                         ids=["above", "exactly-at-reserve", "just-below", "empty"])
def test_reserve_protection_boundary(battery_kwh, points):
    assert score(battery_kwh=battery_kwh)["criteria"]["reserve_protection"] == points


"""
# -----------------------------------------------------------------------------------
# Function: test_response_time_window
# -----------------------------------------------------------------------------------
Description:
    Full points up to 30 s, falling linearly to 0 at 120 s, never negative
    or above full, and rounded to 2 decimal places.

"""
@pytest.mark.parametrize(("seconds", "points"), [
    (0.0, 25.0), (FULL_TIME_CREDIT_S, 25.0), (75.0, 12.5), (100.0, 5.56),
    (ZERO_TIME_CREDIT_S, 0.0), (600.0, 0.0),
], ids=["instant", "full-credit-edge", "halfway", "rounded", "zero-credit-edge", "very-late"])
def test_response_time_window(seconds, points):
    assert score(response_time_s=seconds)["criteria"]["response_time"] == points


"""
# -----------------------------------------------------------------------------------
# Function: test_energy_outcome_against_target_loops
# -----------------------------------------------------------------------------------
Description:
    Points scale with loops restored up to the 20-loop target and are
    capped there; unknown loops (None) and 0 loops score 0; results are
    rounded to 2 decimal places.

"""
@pytest.mark.parametrize(("loops", "points"), [
    (42, 25.0), (TARGET_LOOPS, 25.0), (10, 12.5), (7, 8.75), (1, 1.25), (0, 0.0), (None, 0),
], ids=["above-target", "at-target", "half", "rounded", "one-loop", "zero", "unknown"])
def test_energy_outcome_against_target_loops(loops, points):
    assert score(loops_after=loops)["criteria"]["energy_outcome"] == points


"""
# -----------------------------------------------------------------------------------
# Function: test_total_is_the_sum_of_criteria
# -----------------------------------------------------------------------------------
Description:
    A late, below-reserve decision restoring half the target after one
    rejected try totals the sum of its criteria (0 + 12.5 + 12.5 + 10 = 35).

"""
def test_total_is_the_sum_of_criteria():
    result = score(battery_kwh=5.0, loops_after=10, response_time_s=75.0, rejected_attempts=1)

    assert result["criteria"] == {"reserve_protection": 0, "response_time": 12.5, "energy_outcome": 12.5,
                                  "attempts": 10}
    assert result["total"] == 35.0


"""
# -----------------------------------------------------------------------------------
# Function: test_rejected_attempts_cost_points
# -----------------------------------------------------------------------------------
Description:
    Each rejected battery command before the accepted one costs 5 of the
    15 attempt points, never going below 0.

"""
@pytest.mark.parametrize(("rejected", "points"), [(0, 15), (1, 10), (2, 5), (3, 0), (4, 0)],
                         ids=["first-try", "one", "two", "three", "floor"])
def test_rejected_attempts_cost_points(rejected, points):
    assert score(rejected_attempts=rejected)["criteria"]["attempts"] == points
