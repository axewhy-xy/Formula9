"""
Provisional score of a trainee's charging decision.

Author: Choo Jian Wei
Date Created: 26/10/2026
Last Modified: 26/10/2026

Description:
    Part of BE-M2-05 (battery decision). Scores a battery decision
    (RESERVE_CHARGER or CONTINUE_SERVICE) with the versioned
    m2-battery-provisional-r2 rubric, the battery counterpart of the
    incident service's provisional assessment. The result is recorded in
    the CHARGER_RESERVED or SERVICE_CONTINUED event.

    The rubric is an authored training rule, not a calibrated assessment:
    reserve protection (decided before the shuttle fell below its reserve),
    response time (full credit within 30 s, none after 120 s) and energy
    outcome (loops restored against a 20-loop target) and attempts (rejected
    tries before the accepted one). Every result is marked provisional and
    uncalibrated.
"""
from contracts.v2.models import ChargeOption, FleetVehicle
from .forecast import reserve_margin_kwh
from .model import EnergyModel

RUBRIC_REVISION = "m2-battery-provisional-r2"
RUBRIC = {"reserve_protection": 35, "response_time": 25, "energy_outcome": 25, "attempts": 15}
POINTS_PER_REJECTED_ATTEMPT = 5
FULL_TIME_CREDIT_S = 30.0
ZERO_TIME_CREDIT_S = 120.0
TARGET_LOOPS = 20
ASSUMPTIONS = [
    "Uncalibrated authored training rubric; points are not a real-world assessment.",
    "Reserve protection: deciding before the shuttle falls below its training reserve.",
    "Response time is measured from when the option was offered, in simulation time.",
    "Energy outcome compares loops restored against a 20-loop target.",
    "Attempts: each rejected battery command for this decision costs 5 points.",
]

"""
# -----------------------------------------------------------------------------------
# Function: score_charge_decision
# -----------------------------------------------------------------------------------
Description:
    This function scores one charging decision as it was made: the
    shuttle's battery at the decision, the chosen option's consequence,
    how long the trainee took after the option was offered, and how many
    of their battery commands for this decision were rejected first.

Args:
    model (EnergyModel): The energy parameters (for the reserve).
    vehicle (FleetVehicle): The shuttle at the moment of the decision.
    option (ChargeOption): The chosen charge option.
    response_time_s (float): Seconds from the offer to the decision.
    rejected_attempts (int): Rejected battery commands for the shuttle
        since the decision was first offered.

Returns:
    dict: The assessment: rubric revision, provisional/uncalibrated flags,
        points per criterion, maximum points, total and assumptions.
"""
def score_charge_decision(model: EnergyModel, vehicle: FleetVehicle, option: ChargeOption,
                          response_time_s: float, rejected_attempts: int = 0) -> dict:
    margin = reserve_margin_kwh(model, vehicle.battery_kwh, vehicle.battery_capacity_kwh)
    reserve_points = RUBRIC["reserve_protection"] if margin >= 0 else 0

    fraction = (ZERO_TIME_CREDIT_S - response_time_s) / (ZERO_TIME_CREDIT_S - FULL_TIME_CREDIT_S)
    fraction = max(0.0, min(1.0, fraction))
    time_points = round(fraction * RUBRIC["response_time"], 2)

    if option.loops_after is None:
        energy_points = 0
    else:
        energy_points = round(RUBRIC["energy_outcome"] * min(1.0, option.loops_after / TARGET_LOOPS), 2)

    attempt_points = max(0, RUBRIC["attempts"] - POINTS_PER_REJECTED_ATTEMPT * rejected_attempts)

    criteria = {
        "reserve_protection": reserve_points,
        "response_time": time_points,
        "energy_outcome": energy_points,
        "attempts": attempt_points,
    }

    return {"rubric_revision": RUBRIC_REVISION, "provisional": True, "uncalibrated": True,
            "scope": "BATTERY_DECISION", "criteria": criteria, "maximum_points": RUBRIC,
            "total": round(sum(criteria.values()), 2), "assumptions": ASSUMPTIONS}
