"""
Battery reserve and loops-remaining forecasts for the fleet training simulation.

Author: Choo Jian Wei
Date Created: 22/10/2026
Last Modified: 22/10/2026

Description:
    Part of BE-M2-05 (energy). Answers "how long can this shuttle keep
    going before it should charge?" for the battery screen (NAT-M2-06) and
    reserve warnings.

    The reserve is a training rule (a share of capacity, from the
    EnergyModel), not a physical limit: a shuttle may go below it, and
    that breach must be shown and scored rather than hidden. So the
    margin above reserve can be negative.

    A loop is one full circuit of a shuttle's route. Loops remaining
    counts only complete loops that fit in the margin above reserve.
    Values are simulation estimates based on the versioned EnergyModel.
"""
import math
from .model import EnergyModel,auxiliary_energy_kwh,drive_energy_kwh,require_non_negative

"""
# -----------------------------------------------------------------------------------
# Function: reserve_kwh
# -----------------------------------------------------------------------------------
Description:
    This function returns the training reserve level for a battery.

Args:
    model (EnergyModel): The energy parameters (reserve_fraction).
    capacity_kwh (float): Battery capacity, in kWh.

Returns:
    float: The reserve, in kWh (capacity x reserve_fraction).

Raises:
    ValueError: If capacity_kwh is NaN, infinite or not greater than 0.
"""
def reserve_kwh(model:EnergyModel, capacity_kwh: float)-> float:
    require_non_negative("capacity_kwh", capacity_kwh)
    if capacity_kwh == 0:
        raise ValueError("capacity_kwh must be greater than 0.")
    return capacity_kwh * model.reserve_fraction

"""
# -----------------------------------------------------------------------------------
# Function: loop_energy_kwh
# -----------------------------------------------------------------------------------
Description:
    This function returns the energy needed for one full loop of a route:
    driving energy for its distance plus auxiliary energy for its duration.

Args:
    model (EnergyModel): The energy parameters.
    distance_m (float): Loop length, in metres (RouteRevision.distance_m).
    duration_s (float): Loop time, in seconds
        (RouteRevision.estimated_duration_s).

Returns:
    float: Energy for one loop, in kWh.

Raises:
    ValueError: If distance_m or duration_s is negative.
"""
def loop_energy_kwh (model: EnergyModel, distance_m: float, duration_s: float)-> float:
    return drive_energy_kwh(model, distance_m)+ auxiliary_energy_kwh(model,duration_s)

"""
# -----------------------------------------------------------------------------------
# Function: reserve_margin_kwh
# -----------------------------------------------------------------------------------
Description:
    This function returns how far the battery is above the reserve. A
    negative result means the shuttle is below reserve; it is not clamped
    to 0 so the breach can be shown and scored.

Args:
    model (EnergyModel): The energy parameters.
    battery_kwh (float): Current battery level, in kWh.
    capacity_kwh (float): Battery capacity, in kWh.

Returns:
    float: Battery minus reserve, in kWh (negative if below reserve).

Raises:
    ValueError: If battery_kwh is NaN, infinite or negative, or
        capacity_kwh is NaN, infinite or not greater than 0.
"""
def reserve_margin_kwh(model: EnergyModel, battery_kwh: float, capacity_kwh: float)-> float:
    require_non_negative("battery_kwh", battery_kwh)
    return battery_kwh- reserve_kwh(model,capacity_kwh)

"""
# -----------------------------------------------------------------------------------
# Function: loops_remaining
# -----------------------------------------------------------------------------------
Description:
    This function returns how many complete loops fit in the margin above
    the reserve. A partial loop does not count, so the result is rounded
    down. A tiny tolerance stops floating-point error (e.g. 0.9999999999)
    from losing a whole loop at an exact boundary.

    A loop that uses no energy (e.g. a zero-length route, which the v2
    contract allows) cannot be forecast, so the result is None meaning
    "forecast unavailable" rather than a crash or an invented number.

Args:
    model (EnergyModel): The energy parameters.
    battery_kwh (float): Current battery level, in kWh.
    capacity_kwh (float): Battery capacity, in kWh.
    loop_kwh (float): Energy for one loop, in kWh (see loop_energy_kwh).

Returns:
    int | None: Complete loops before reaching the reserve; 0 if already
    at or below it; None if loop_kwh is 0 (forecast unavailable).

Raises:
    ValueError: If loop_kwh is NaN, infinite or negative, or the battery
        or capacity is invalid (see reserve_margin_kwh).
"""
def loops_remaining(model:EnergyModel, battery_kwh: float,capacity_kwh: float, loop_kwh:float)->int | None:
    require_non_negative("loop_kwh", loop_kwh)
    if loop_kwh == 0:
        return None
    margin = reserve_margin_kwh (model, battery_kwh,capacity_kwh)
    if margin <=0:
        return 0
    return math.floor (margin/loop_kwh+ 1e-9)