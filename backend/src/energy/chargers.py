"""
Charger port reservations for the fleet training simulation.

Author: Choo Jian Wei
Date Created: 22/10/2026
Last Modified: 22/10/2026

Description:
    Part of BE-M2-05 (battery decision). Mirrors the v2 contract's Charger
    (charger_id, port_count, occupied_vehicle_ids) and enforces the M2
    booking rules:

      1. A charger cannot hold more shuttles than it has ports.
      2. A shuttle cannot occupy two chargers at once.

    Breaking a rule raises ChargerConflict, which the session actor turns
    into the contract's CHARGER_CONFLICT rejection so the trainee sees a
    clear reason. Wrong IDs or releasing an unbooked shuttle are caller
    errors and raise ValueError instead.

    Functions never change a ChargerState in place; they return a new list
    of chargers. Time-slot reservations are in bookings.py; swaps and outages
    are M3.
"""
from dataclasses import dataclass


# -----------------------------------------------------------------------------------
# Class: ChargerConflict
# -----------------------------------------------------------------------------------
"""
Description:
    Raised when a booking breaks a charger rule (charger full, or the
    shuttle is already on a charger). Separate from ValueError so callers
    can catch booking clashes without hiding programming errors.

Attributes:
    code: The v2 contract error code, "CHARGER_CONFLICT".
"""
class ChargerConflict(Exception):
    """A charger booking clashes with current occupancy (contract code CHARGER_CONFLICT)."""
    code = "CHARGER_CONFLICT"


# -----------------------------------------------------------------------------------
# Class: ChargerState
# -----------------------------------------------------------------------------------
"""
Description:
    This class holds one charger's ports and the shuttles plugged into it.
    It is immutable: occupancy is stored as a tuple (a list from contract
    JSON is converted), and invalid states are rejected when the charger
    is created.

Attributes:
    charger_id: Stable charger ID, e.g. "charger-1".
    port_count: Number of shuttles it can charge at once (at least 1).
    occupied_vehicle_ids: Shuttles currently plugged in; no more than
        port_count and no duplicates.
"""
@dataclass(frozen=True)
class ChargerState:
    charger_id: str
    port_count: int
    occupied_vehicle_ids: tuple[str, ...]

    def __post_init__(self) -> None:
        # Contract JSON stores occupancy as a list; keep an immutable tuple so
        # bookings can extend it and nobody can change it in place. The class
        # is frozen, so the field is set through object.__setattr__.
        object.__setattr__(self, "occupied_vehicle_ids", tuple(self.occupied_vehicle_ids))
        if self.port_count < 1:
            raise ValueError("Charger needs at least one port")
        if len(self.occupied_vehicle_ids) > self.port_count:
            raise ValueError("More occupied shuttles than ports")
        if len(set(self.occupied_vehicle_ids)) != len(self.occupied_vehicle_ids):
            raise ValueError("a vehicle is listed twice on the same charger")


"""
# -----------------------------------------------------------------------------------
# Function: free_ports
# -----------------------------------------------------------------------------------
Description:
    This function returns how many ports on a charger are free.

Args:
    charger (ChargerState): The charger.

Returns:
    int: port_count minus the number of occupied ports.
"""
def free_ports(charger: ChargerState) -> int:
    return charger.port_count - len(charger.occupied_vehicle_ids)


"""
# -----------------------------------------------------------------------------------
# Function: reserve_port
# -----------------------------------------------------------------------------------
Description:
    This function plugs a shuttle into a charger. It checks all chargers,
    because a shuttle may only occupy one at a time.

Args:
    chargers (list[ChargerState]): Every charger in the session.
    charger_id (str): The charger to book.
    vehicle_id (str): The shuttle to plug in.

Returns:
    list[ChargerState]: A new list with the shuttle added to the charger;
    the input list is unchanged.

Raises:
    ValueError: If charger_id does not exist.
    ChargerConflict: If the shuttle is already on a charger, or the
        charger has no free port.
"""
def reserve_port(chargers: list[ChargerState], charger_id: str, vehicle_id: str) -> list[ChargerState]:
    target = next((c for c in chargers if c.charger_id == charger_id), None)
    if target is None:
        raise ValueError(f"unknown charger {charger_id}")
    if any(vehicle_id in c.occupied_vehicle_ids for c in chargers):
        raise ChargerConflict(f"{vehicle_id} is already on a charger")
    if free_ports(target) == 0:
        raise ChargerConflict(f"{charger_id} has no free port")
    new_target = ChargerState(target.charger_id, target.port_count,
                              target.occupied_vehicle_ids + (vehicle_id,))
    return [new_target if c.charger_id == charger_id else c for c in chargers]


"""
# -----------------------------------------------------------------------------------
# Function: release_port
# -----------------------------------------------------------------------------------
Description:
    This function unplugs a shuttle from a charger, freeing its port.

Args:
    chargers (list[ChargerState]): Every charger in the session.
    charger_id (str): The charger to release from.
    vehicle_id (str): The shuttle to unplug.

Returns:
    list[ChargerState]: A new list with the shuttle removed from the
    charger; the input list is unchanged.

Raises:
    ValueError: If charger_id does not exist, or the shuttle is not on
        that charger.
"""
def release_port(chargers: list[ChargerState], charger_id: str, vehicle_id: str) -> list[ChargerState]:
    target = next((c for c in chargers if c.charger_id == charger_id), None)
    if target is None:
        raise ValueError(f"unknown charger {charger_id}")
    if vehicle_id not in target.occupied_vehicle_ids:
        raise ValueError(f"{vehicle_id} isn't on {charger_id}.")
    remaining = tuple(v for v in target.occupied_vehicle_ids if v != vehicle_id)
    new_target = ChargerState(target.charger_id, target.port_count,remaining)
    return [new_target if c.charger_id == charger_id else c for c in chargers]
    