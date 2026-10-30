"""
Battery energy use and charging for the fleet training simulation.

Author: Choo Jian Wei
Date Created: 22/10/2026
Last Modified: 26/10/2026

Description:
    Part of BE-M2-05 (energy). Pure functions the session actor calls each
    simulation tick: driving uses energy per kilometre, auxiliary systems
    (air-con, computers) use energy over time even while parked, and
    charging adds charger power x time x efficiency up to capacity.

    Units follow the v2 contract: energy in kWh, power in kW, distance in
    metres, time in seconds. All parameters live in a versioned
    EnergyModel (the scenario's energy_model_revision). The values in
    ENERGY_R1 are configurable training assumptions, not measured shuttle
    data.

    The battery is physically bounded at 0 kWh, but running out is
    reported rather than hidden so it can be scored. The training reserve
    is a separate rule handled in forecast.py.
"""
import math
from dataclasses import dataclass


"""
# -----------------------------------------------------------------------------------
# Function: require_non_negative
# -----------------------------------------------------------------------------------
Description:
    This function rejects a value that is NaN, infinite or negative. NaN
    slips past a plain "< 0" check (NaN < 0 is False), and one NaN would
    turn every later battery reading into NaN, so every energy input goes
    through this check.

Args:
    name (str): The parameter name, used in the error message.
    value (float): The value to check.

Raises:
    ValueError: If value is NaN, infinite or negative.
"""
def require_non_negative(name: str, value: float) -> None:
    if not math.isfinite(value):
        raise ValueError(f"{name} must be a finite number")
    if value < 0:
        raise ValueError(f"{name} must not be negative")


# -----------------------------------------------------------------------------------
# Class: EnergyModel
# -----------------------------------------------------------------------------------
"""
Description:
    This class holds one versioned set of energy parameters. Invalid values
    (negative, out of range, NaN or infinite) are rejected when the model
    is created, so a bad scenario fails early instead of producing nonsense
    energy.

Attributes:
    revision: Version ID, matching the scenario's energy_model_revision.
    drive_kwh_per_km: Energy used per kilometre driven (>= 0).
    auxiliary_kw: Constant power of auxiliary systems, used even when
        parked (>= 0).
    charge_efficiency: Share of charger energy that reaches the battery,
        above 0 and at most 1.
    reserve_fraction: Training reserve as a share of capacity, at least 0
        and below 1.
"""
@dataclass(frozen=True)
class EnergyModel:
    revision: str
    drive_kwh_per_km: float
    auxiliary_kw: float
    charge_efficiency:float
    reserve_fraction: float

    def __post_init__(self)->(None):
        require_non_negative("drive_kwh_per_km", self.drive_kwh_per_km)
        require_non_negative("auxiliary_kw", self.auxiliary_kw)
        if not math.isfinite(self.charge_efficiency) or not math.isfinite(self.reserve_fraction):
            raise ValueError("charge_efficiency and reserve_fraction must be finite numbers")
        if not 0<self.charge_efficiency<=1:
            raise ValueError("charge_efficiency must be between 0 and at least 1")
        if not 0<=self.reserve_fraction<1:
            raise ValueError("reserve_fraction must be at least 0 and below 1")


# Default training parameters for the M2 fixture (energy_model_revision
# "energy-r1"). Configurable assumptions, not measured shuttle data.
ENERGY_R1 = EnergyModel(
    revision ="energy-r1",
    drive_kwh_per_km= 0.25,
    auxiliary_kw=1.5,
    charge_efficiency= 0.9,
    reserve_fraction= 0.15,
)

# Energy models by revision, so a session uses the model its scenario names
# (scenario.energy_model_revision). Add new revisions here.
ENERGY_MODELS = {ENERGY_R1.revision: ENERGY_R1}

"""
# -----------------------------------------------------------------------------------
# Function: drive_energy_kwh
# -----------------------------------------------------------------------------------
Description:
    This function returns the energy used by driving a distance. It does
    not change the battery; use_energy subtracts it.

Args:
    model (EnergyModel): The energy parameters.
    distance_m (float): Distance driven, in metres.

Returns:
    float: Energy used, in kWh.

Raises:
    ValueError: If distance_m is NaN, infinite or negative.
"""
def drive_energy_kwh(model:EnergyModel,distance_m:float)-> float:
    require_non_negative("distance_m", distance_m)
    return distance_m/1000 * model.drive_kwh_per_km

"""
# -----------------------------------------------------------------------------------
# Function: auxiliary_energy_kwh
# -----------------------------------------------------------------------------------
Description:
    This function returns the energy used by auxiliary systems (air-con,
    computers) over a period of time. It applies whether or not the
    shuttle is moving, so a long wait still drains the battery.

Args:
    model (EnergyModel): The energy parameters.
    duration_s (float): Elapsed simulation time, in seconds.

Returns:
    float: Energy used, in kWh.

Raises:
    ValueError: If duration_s is NaN, infinite or negative.
"""
def auxiliary_energy_kwh(model: EnergyModel, duration_s :float)-> float:
    require_non_negative("duration_s", duration_s)
    return duration_s/3600 *model.auxiliary_kw

"""
# -----------------------------------------------------------------------------------
# Function: charge_battery_kwh
# -----------------------------------------------------------------------------------
Description:
    This function returns the battery level after charging. The energy
    that reaches the battery is charger power x time x efficiency, and the
    result never exceeds capacity.

Args:
    model (EnergyModel): The energy parameters.
    battery_kwh (float): Battery level before charging, in kWh.
    capacity_kwh (float): Battery capacity, in kWh.
    charger_power_kw (float): Charger power, in kW (the contract's
        Charger.power_kw).
    duration_s (float): Charging time, in seconds.

Returns:
    float: The new battery level, in kWh, capped at capacity_kwh.

Raises:
    ValueError: If any input is NaN or infinite, battery_kwh,
        charger_power_kw or duration_s is negative, or capacity_kwh is not
        greater than 0.
"""
def charge_battery_kwh(model: EnergyModel,battery_kwh: float,capacity_kwh: float, charger_power_kw: float,duration_s : float)->float:
    require_non_negative("battery_kwh", battery_kwh)
    require_non_negative("capacity_kwh", capacity_kwh)
    if capacity_kwh == 0:
        raise ValueError("capacity_kwh must be greater than 0")
    require_non_negative("charger_power_kw", charger_power_kw)
    require_non_negative("duration_s", duration_s)
    energy_reaches_battery = charger_power_kw * (duration_s/3600) * model.charge_efficiency
    new_level = battery_kwh + energy_reaches_battery
    return float(min(new_level,capacity_kwh))

"""
# -----------------------------------------------------------------------------------
# Function: use_energy
# -----------------------------------------------------------------------------------
Description:
    This function applies one step of energy use (driving plus auxiliary)
    to a battery. The level stops at 0 kWh, but running out is reported in
    ran_out so it can be scored rather than hidden. Using exactly the
    remaining energy is not running out. The training reserve is not
    checked here; see forecast.py.

Args:
    model (EnergyModel): The energy parameters.
    battery_kwh (float): Battery level before this step, in kWh.
    distance_m (float): Distance driven during the step, in metres.
    duration_s (float): Length of the step, in seconds.

Returns:
    tuple[float, bool]: The new battery level in kWh (never below 0), and
    True if the step needed more energy than the battery had.

Raises:
    ValueError: If battery_kwh, distance_m or duration_s is NaN, infinite
        or negative.
"""
def use_energy(model: EnergyModel, battery_kwh: float, distance_m:float, duration_s : float)-> tuple[float,bool]:
    require_non_negative("battery_kwh", battery_kwh)
    Total_Used = drive_energy_kwh(model,distance_m)+auxiliary_energy_kwh(model,duration_s)
    New_level= battery_kwh-Total_Used
    ran_out = Total_Used>battery_kwh
    return max(New_level,0.0),ran_out