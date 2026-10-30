"""
Unit tests for the energy package (model.py, forecast.py, chargers.py).

Author: Choo Jian Wei
Date: 22/10/2026
Last Modified: 22/10/2026

Description:
    Tests the BE-M2-05 battery model: energy used by driving and auxiliary
    systems, charging, running out, the training reserve, loops remaining,
    and charger port reservations. Covers the cases the architecture
    requires (zero distance, prolonged waiting, interrupted charge, empty
    battery, detour longer than the loop) and checks the values against
    the shared v2 snapshot fixture, so a fixture change is noticed.

"""

import json
from pathlib import Path

import pytest

from backend.src.energy.chargers import (
    ChargerConflict,
    ChargerState,
    free_ports,
    release_port,
    reserve_port,
)
from backend.src.energy.forecast import (
    loop_energy_kwh,
    loops_remaining,
    reserve_kwh,
    reserve_margin_kwh,
)
from backend.src.energy.model import (
    ENERGY_R1,
    EnergyModel,
    auxiliary_energy_kwh,
    charge_battery_kwh,
    drive_energy_kwh,
    use_energy,
)

SNAPSHOT_FIXTURE = (
    Path(__file__).resolve().parents[2] / "contracts" / "v2" / "fixtures" / "snapshot.json"
)

# route-a from the fixture: 1200 m in 180 s, which needs 0.375 kWh per loop
# under ENERGY_R1 (0.3 kWh driving + 0.075 kWh auxiliary).
LOOP_M = 1200.0
LOOP_S = 180.0
LOOP_KWH = 0.375


def _snapshot():
    return json.loads(SNAPSHOT_FIXTURE.read_text(encoding="utf-8"))


# ===================================================================================
# model.py
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_energy_model_rejects_invalid_parameters
# -----------------------------------------------------------------------------------
Description:
    A scenario with nonsense energy parameters fails when the model is
    created rather than producing nonsense energy later.

"""
@pytest.mark.parametrize(
    "args",
    [
        ("bad", -0.1, 1.5, 0.9, 0.15),   # negative driving consumption
        ("bad", 0.25, -1.0, 0.9, 0.15),  # negative auxiliary power
        ("bad", 0.25, 1.5, 0.0, 0.15),   # zero efficiency adds nothing
        ("bad", 0.25, 1.5, 1.2, 0.15),   # efficiency above 100%
        ("bad", 0.25, 1.5, 0.9, -0.1),   # negative reserve
        ("bad", 0.25, 1.5, 0.9, 1.0),    # whole battery as reserve
        ("bad", float("nan"), 1.5, 0.9, 0.15),   # NaN slips past "< 0"
        ("bad", float("inf"), 1.5, 0.9, 0.15),
        ("bad", 0.25, float("nan"), 0.9, 0.15),
        ("bad", 0.25, float("inf"), 0.9, 0.15),
        ("bad", 0.25, 1.5, float("nan"), 0.15),
        ("bad", 0.25, 1.5, 0.9, float("nan")),
    ],
)
def test_energy_model_rejects_invalid_parameters(args):
    with pytest.raises(ValueError):
        EnergyModel(*args)


"""
# -----------------------------------------------------------------------------------
# Function: test_energy_model_accepts_boundary_values
# -----------------------------------------------------------------------------------
Description:
    A perfect charger (efficiency 1) and no reserve (0) are valid setups.

"""
def test_energy_model_accepts_boundary_values():
    model = EnergyModel("edge", 0.0, 0.0, 1.0, 0.0)

    assert model.charge_efficiency == 1.0
    assert model.reserve_fraction == 0.0


"""
# -----------------------------------------------------------------------------------
# Function: test_energy_r1_matches_fixture_revision
# -----------------------------------------------------------------------------------
Description:
    ENERGY_R1 is the model the shared fixture's scenario refers to.

"""
def test_energy_r1_matches_fixture_revision():
    assert ENERGY_R1.revision == _snapshot()["scenario"]["energy_model_revision"]


"""
# -----------------------------------------------------------------------------------
# Function: test_drive_and_auxiliary_energy_known_values
# -----------------------------------------------------------------------------------
Description:
    One fixture loop: 1.2 km x 0.25 kWh/km and 0.05 h x 1.5 kW. Zero
    distance or time uses nothing.

"""
def test_drive_and_auxiliary_energy_known_values():
    assert drive_energy_kwh(ENERGY_R1, LOOP_M) == pytest.approx(0.3)
    assert auxiliary_energy_kwh(ENERGY_R1, LOOP_S) == pytest.approx(0.075)
    assert drive_energy_kwh(ENERGY_R1, 0) == 0.0
    assert auxiliary_energy_kwh(ENERGY_R1, 0) == 0.0


"""
# -----------------------------------------------------------------------------------
# Function: test_negative_inputs_are_rejected
# -----------------------------------------------------------------------------------
Description:
    Negative distance, time or charger power is a caller bug; without the
    check, a negative charger power would drain the battery while
    "charging".

"""
@pytest.mark.parametrize(
    "call",
    [
        lambda: drive_energy_kwh(ENERGY_R1, -1),
        lambda: auxiliary_energy_kwh(ENERGY_R1, -1),
        lambda: charge_battery_kwh(ENERGY_R1, 28, 40, -50, 600),
        lambda: charge_battery_kwh(ENERGY_R1, 28, 40, 50, -1),
        lambda: use_energy(ENERGY_R1, 28, -1, 180),
        lambda: use_energy(ENERGY_R1, 28, 1200, -1),
    ],
    ids=["drive", "auxiliary", "charge-power", "charge-time", "use-distance", "use-time"],
)
def test_negative_inputs_are_rejected(call):
    with pytest.raises(ValueError):
        call()


"""
# -----------------------------------------------------------------------------------
# Function: test_non_finite_inputs_are_rejected
# -----------------------------------------------------------------------------------
Description:
    NaN or infinity as a distance, time, power or battery level would make
    the battery NaN from then on, so every energy input rejects them.

"""
@pytest.mark.parametrize("bad", [float("nan"), float("inf")], ids=["nan", "inf"])
@pytest.mark.parametrize(
    "call",
    [
        lambda bad: drive_energy_kwh(ENERGY_R1, bad),
        lambda bad: auxiliary_energy_kwh(ENERGY_R1, bad),
        lambda bad: charge_battery_kwh(ENERGY_R1, bad, 40, 50, 600),
        lambda bad: charge_battery_kwh(ENERGY_R1, 28, bad, 50, 600),
        lambda bad: charge_battery_kwh(ENERGY_R1, 28, 40, bad, 600),
        lambda bad: charge_battery_kwh(ENERGY_R1, 28, 40, 50, bad),
        lambda bad: use_energy(ENERGY_R1, bad, 1200, 180),
        lambda bad: use_energy(ENERGY_R1, 28, bad, 180),
        lambda bad: reserve_kwh(ENERGY_R1, bad),
        lambda bad: reserve_margin_kwh(ENERGY_R1, bad, 40),
        lambda bad: loops_remaining(ENERGY_R1, 28, 40, bad),
    ],
    ids=["drive", "auxiliary", "charge-battery", "charge-capacity", "charge-power",
         "charge-time", "use-battery", "use-distance", "reserve-capacity",
         "margin-battery", "loop-energy"],
)
def test_non_finite_inputs_are_rejected(call, bad):
    with pytest.raises(ValueError):
        call(bad)


"""
# -----------------------------------------------------------------------------------
# Function: test_charge_adds_power_time_efficiency_up_to_capacity
# -----------------------------------------------------------------------------------
Description:
    50 kW for 10 minutes at 90% adds 7.5 kWh; charging stops at capacity;
    zero time changes nothing; the result is always a float.

"""
def test_charge_adds_power_time_efficiency_up_to_capacity():
    assert charge_battery_kwh(ENERGY_R1, 28, 40, 50, 600) == pytest.approx(35.5)
    assert charge_battery_kwh(ENERGY_R1, 38, 40, 50, 600) == 40.0
    assert charge_battery_kwh(ENERGY_R1, 28, 40, 50, 0) == 28.0
    assert type(charge_battery_kwh(ENERGY_R1, 38, 40, 50, 600)) is float


"""
# -----------------------------------------------------------------------------------
# Function: test_interrupted_charge_equals_continuous_charge
# -----------------------------------------------------------------------------------
Description:
    Two 5-minute charges add the same energy as one 10-minute charge, so
    pausing a charge (e.g. a charger outage) does not create or lose energy.

"""
def test_interrupted_charge_equals_continuous_charge():
    continuous = charge_battery_kwh(ENERGY_R1, 20, 40, 50, 600)
    halfway = charge_battery_kwh(ENERGY_R1, 20, 40, 50, 300)
    interrupted = charge_battery_kwh(ENERGY_R1, halfway, 40, 50, 300)

    assert interrupted == pytest.approx(continuous)


"""
# -----------------------------------------------------------------------------------
# Function: test_use_energy_known_cases
# -----------------------------------------------------------------------------------
Description:
    A normal loop, standing still for no time, running out, and using
    exactly what is left (which is not running out).

"""
@pytest.mark.parametrize(
    ("battery", "distance", "duration", "expected_battery", "expected_ran_out"),
    [
        (28.0, LOOP_M, LOOP_S, 27.625, False),
        (28.0, 0, 0, 28.0, False),
        (0.2, LOOP_M, LOOP_S, 0.0, True),
        (LOOP_KWH, LOOP_M, LOOP_S, 0.0, False),
    ],
    ids=["normal-loop", "no-use", "runs-out", "exactly-empty"],
)
def test_use_energy_known_cases(battery, distance, duration, expected_battery, expected_ran_out):
    new_battery, ran_out = use_energy(ENERGY_R1, battery, distance, duration)

    assert new_battery == pytest.approx(expected_battery)
    assert ran_out is expected_ran_out


"""
# -----------------------------------------------------------------------------------
# Function: test_prolonged_waiting_still_drains_battery
# -----------------------------------------------------------------------------------
Description:
    A shuttle parked for an hour (e.g. waiting for guidance) uses its
    auxiliary power: 1.5 kW for 1 h is 1.5 kWh.

"""
def test_prolonged_waiting_still_drains_battery():
    new_battery, ran_out = use_energy(ENERGY_R1, 10.0, 0, 3600)

    assert new_battery == pytest.approx(8.5)
    assert ran_out is False


"""
# -----------------------------------------------------------------------------------
# Function: test_empty_battery_never_goes_negative
# -----------------------------------------------------------------------------------
Description:
    Driving on an empty battery stays at 0 kWh but keeps reporting that
    the shuttle has run out.

"""
def test_empty_battery_never_goes_negative():
    new_battery, ran_out = use_energy(ENERGY_R1, 0.0, LOOP_M, LOOP_S)

    assert new_battery == 0.0
    assert ran_out is True


"""
# -----------------------------------------------------------------------------------
# Function: test_tick_by_tick_use_matches_whole_loop
# -----------------------------------------------------------------------------------
Description:
    The session actor calls use_energy every 50 ms tick. 3,600 ticks over
    one loop must use the same energy as the loop computed in one go.

"""
def test_tick_by_tick_use_matches_whole_loop():
    ticks = int(LOOP_S / 0.05)
    battery = 28.0
    for _ in range(ticks):
        battery, _ = use_energy(ENERGY_R1, battery, LOOP_M / ticks, 0.05)

    assert battery == pytest.approx(28.0 - LOOP_KWH)


# ===================================================================================
# forecast.py
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_reserve_and_margin
# -----------------------------------------------------------------------------------
Description:
    15% of 40 kWh is a 6 kWh reserve. The margin is negative below the
    reserve, so a breach is visible rather than clamped to 0.

"""
def test_reserve_and_margin():
    assert reserve_kwh(ENERGY_R1, 40) == pytest.approx(6.0)
    assert reserve_margin_kwh(ENERGY_R1, 28, 40) == pytest.approx(22.0)
    assert reserve_margin_kwh(ENERGY_R1, 4, 40) == pytest.approx(-2.0)


"""
# -----------------------------------------------------------------------------------
# Function: test_reserve_rejects_invalid_capacity
# -----------------------------------------------------------------------------------
Description:
    A battery with no capacity is a broken scenario.

"""
@pytest.mark.parametrize("capacity", [0, -40])
def test_reserve_rejects_invalid_capacity(capacity):
    with pytest.raises(ValueError):
        reserve_kwh(ENERGY_R1, capacity)


"""
# -----------------------------------------------------------------------------------
# Function: test_loop_energy_for_fixture_route
# -----------------------------------------------------------------------------------
Description:
    The fixture route's own distance and duration give 0.375 kWh per loop.

"""
def test_loop_energy_for_fixture_route():
    route = _snapshot()["routes"][0]

    assert loop_energy_kwh(ENERGY_R1, route["distance_m"], route["estimated_duration_s"]) == (
        pytest.approx(LOOP_KWH)
    )


"""
# -----------------------------------------------------------------------------------
# Function: test_loops_remaining_known_values
# -----------------------------------------------------------------------------------
Description:
    22 kWh above reserve fits 58 whole loops. Exactly one loop above
    reserve gives 1 (not 0 from floating-point error); at or below the
    reserve gives 0.

"""
@pytest.mark.parametrize(
    ("battery", "expected"),
    [(28.0, 58), (6.375, 1), (6.0, 0), (4.0, 0)],
    ids=["fixture-shuttle", "exactly-one-loop", "at-reserve", "below-reserve"],
)
def test_loops_remaining_known_values(battery, expected):
    assert loops_remaining(ENERGY_R1, battery, 40, LOOP_KWH) == expected


"""
# -----------------------------------------------------------------------------------
# Function: test_loops_remaining_survives_floating_point_error
# -----------------------------------------------------------------------------------
Description:
    0.1 * 3 is stored as 0.30000000000000004, so a battery exactly one such
    loop above the reserve divides to 0.9999999999999992 loops. Without the
    tolerance in loops_remaining this floors to 0 and loses a whole loop.

"""
def test_loops_remaining_survives_floating_point_error():
    loop_kwh = 0.1 * 3
    battery_kwh = 6.0 + 0.3

    assert loops_remaining(ENERGY_R1, battery_kwh, 40, loop_kwh) == 1


"""
# -----------------------------------------------------------------------------------
# Function: test_loops_remaining_rejects_negative_loop_energy
# -----------------------------------------------------------------------------------
Description:
    A loop cannot give energy back; negative loop energy is a caller bug.

"""
def test_loops_remaining_rejects_negative_loop_energy():
    with pytest.raises(ValueError):
        loops_remaining(ENERGY_R1, 28, 40, -0.375)


"""
# -----------------------------------------------------------------------------------
# Function: test_zero_length_route_forecast_is_unavailable
# -----------------------------------------------------------------------------------
Description:
    The v2 contract allows a route with zero distance and duration. Its
    loop uses no energy, so loops remaining cannot be forecast: the result
    is None ("unavailable") instead of a crash on valid contract data.

"""
def test_zero_length_route_forecast_is_unavailable():
    loop = loop_energy_kwh(ENERGY_R1, 0, 0)

    assert loop == 0.0
    assert loops_remaining(ENERGY_R1, 28, 40, loop) is None


"""
# -----------------------------------------------------------------------------------
# Function: test_detour_longer_than_loop_reduces_loops
# -----------------------------------------------------------------------------------
Description:
    A detour twice as long as the loop needs twice the energy, so fewer
    detour loops fit, and a margin that fits one normal loop fits no
    detour loop.

"""
def test_detour_longer_than_loop_reduces_loops():
    detour_kwh = loop_energy_kwh(ENERGY_R1, 2 * LOOP_M, 2 * LOOP_S)

    assert detour_kwh == pytest.approx(2 * LOOP_KWH)
    assert loops_remaining(ENERGY_R1, 28, 40, detour_kwh) == 29
    assert loops_remaining(ENERGY_R1, 6.5, 40, LOOP_KWH) == 1
    assert loops_remaining(ENERGY_R1, 6.5, 40, detour_kwh) == 0


"""
# -----------------------------------------------------------------------------------
# Function: test_fixture_shuttles_forecast
# -----------------------------------------------------------------------------------
Description:
    Every shuttle in the shared fixture forecasts consistently from its own
    battery and capacity fields.

"""
def test_fixture_shuttles_forecast():
    snapshot = _snapshot()
    route = snapshot["routes"][0]
    loop = loop_energy_kwh(ENERGY_R1, route["distance_m"], route["estimated_duration_s"])

    for vehicle in snapshot["vehicles"]:
        assert loops_remaining(
            ENERGY_R1, vehicle["battery_kwh"], vehicle["battery_capacity_kwh"], loop
        ) == 58


# ===================================================================================
# chargers.py
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_charger_state_rejects_invalid_occupancy
# -----------------------------------------------------------------------------------
Description:
    No ports, more shuttles than ports, or the same shuttle listed twice
    are impossible charger states.

"""
@pytest.mark.parametrize(
    "args",
    [("c", 0, ()), ("c", 1, ("a", "b")), ("c", 2, ("a", "a"))],
    ids=["no-ports", "overfull", "duplicate"],
)
def test_charger_state_rejects_invalid_occupancy(args):
    with pytest.raises(ValueError):
        ChargerState(*args)


"""
# -----------------------------------------------------------------------------------
# Function: test_charger_state_accepts_contract_list
# -----------------------------------------------------------------------------------
Description:
    Contract JSON stores occupancy as a list. ChargerState converts it to a
    tuple, so a booking on contract data works instead of raising
    TypeError (list + tuple).

"""
def test_charger_state_accepts_contract_list():
    fixture_charger = dict(_snapshot()["chargers"][0], occupied_vehicle_ids=["shuttle-01"])
    charger = ChargerState(
        fixture_charger["charger_id"],
        fixture_charger["port_count"] + 1,
        fixture_charger["occupied_vehicle_ids"],
    )

    booked = reserve_port([charger], charger.charger_id, "shuttle-02")

    assert charger.occupied_vehicle_ids == ("shuttle-01",)
    assert booked[0].occupied_vehicle_ids == ("shuttle-01", "shuttle-02")


"""
# -----------------------------------------------------------------------------------
# Function: test_charger_conflict_carries_contract_code
# -----------------------------------------------------------------------------------
Description:
    ChargerConflict can be raised and carries the v2 contract error code.

"""
def test_charger_conflict_carries_contract_code():
    assert issubclass(ChargerConflict, Exception)
    assert ChargerConflict.code == "CHARGER_CONFLICT"


"""
# -----------------------------------------------------------------------------------
# Function: test_free_ports
# -----------------------------------------------------------------------------------
Description:
    Free ports are total ports minus occupied ones.

"""
def test_free_ports():
    assert free_ports(ChargerState("charger-1", 2, ("shuttle-01",))) == 1
    assert free_ports(ChargerState("charger-2", 1, ())) == 1
    assert free_ports(ChargerState("charger-3", 1, ("shuttle-01",))) == 0


"""
# -----------------------------------------------------------------------------------
# Function: test_reserve_port_books_without_changing_input
# -----------------------------------------------------------------------------------
Description:
    A booking returns a new charger list; the original list and its other
    chargers are untouched.

"""
def test_reserve_port_books_without_changing_input():
    chargers = [ChargerState("charger-1", 1, ()), ChargerState("charger-2", 1, ())]

    booked = reserve_port(chargers, "charger-1", "shuttle-01")

    assert booked == [ChargerState("charger-1", 1, ("shuttle-01",)), ChargerState("charger-2", 1, ())]
    assert chargers == [ChargerState("charger-1", 1, ()), ChargerState("charger-2", 1, ())]


"""
# -----------------------------------------------------------------------------------
# Function: test_reserve_port_uses_second_port
# -----------------------------------------------------------------------------------
Description:
    A two-port charger accepts a second shuttle.

"""
def test_reserve_port_uses_second_port():
    booked = reserve_port([ChargerState("charger-1", 2, ("shuttle-01",))], "charger-1", "shuttle-02")

    assert booked[0].occupied_vehicle_ids == ("shuttle-01", "shuttle-02")


"""
# -----------------------------------------------------------------------------------
# Function: test_reserve_port_conflicts
# -----------------------------------------------------------------------------------
Description:
    A full charger, or a shuttle already on another charger, is a
    CHARGER_CONFLICT with a readable reason.

"""
@pytest.mark.parametrize(
    ("charger_id", "vehicle_id", "message"),
    [("charger-1", "shuttle-02", "no free port"), ("charger-2", "shuttle-01", "already on a charger")],
    ids=["charger-full", "shuttle-on-other-charger"],
)
def test_reserve_port_conflicts(charger_id, vehicle_id, message):
    chargers = [ChargerState("charger-1", 1, ("shuttle-01",)), ChargerState("charger-2", 1, ())]

    with pytest.raises(ChargerConflict, match=message):
        reserve_port(chargers, charger_id, vehicle_id)


"""
# -----------------------------------------------------------------------------------
# Function: test_reserve_port_unknown_charger
# -----------------------------------------------------------------------------------
Description:
    A wrong charger ID is a caller error, not a booking clash.

"""
def test_reserve_port_unknown_charger():
    with pytest.raises(ValueError, match="unknown charger"):
        reserve_port([ChargerState("charger-1", 1, ())], "charger-9", "shuttle-01")


"""
# -----------------------------------------------------------------------------------
# Function: test_release_port_frees_only_that_shuttle
# -----------------------------------------------------------------------------------
Description:
    Releasing removes only that shuttle and leaves the input unchanged.

"""
def test_release_port_frees_only_that_shuttle():
    chargers = [ChargerState("charger-1", 2, ("shuttle-01", "shuttle-02"))]

    released = release_port(chargers, "charger-1", "shuttle-01")

    assert released == [ChargerState("charger-1", 2, ("shuttle-02",))]
    assert chargers == [ChargerState("charger-1", 2, ("shuttle-01", "shuttle-02"))]


"""
# -----------------------------------------------------------------------------------
# Function: test_release_port_rejects_wrong_release
# -----------------------------------------------------------------------------------
Description:
    Releasing an unknown charger, a shuttle that is not plugged in, or a
    shuttle from a charger it is not on is a caller error.

"""
@pytest.mark.parametrize(
    ("chargers", "charger_id"),
    [
        ([ChargerState("charger-1", 1, ("shuttle-01",))], "charger-9"),
        ([ChargerState("charger-1", 1, ())], "charger-1"),
        ([ChargerState("charger-1", 1, ()), ChargerState("charger-2", 1, ("shuttle-01",))], "charger-1"),
    ],
    ids=["unknown-charger", "not-plugged-in", "on-other-charger"],
)
def test_release_port_rejects_wrong_release(chargers, charger_id):
    with pytest.raises(ValueError):
        release_port(chargers, charger_id, "shuttle-01")


"""
# -----------------------------------------------------------------------------------
# Function: test_freed_port_can_be_booked_again
# -----------------------------------------------------------------------------------
Description:
    The real flow: one shuttle charges and leaves, the next plugs in, using
    the fixture's own charger.

"""
def test_freed_port_can_be_booked_again():
    fixture_charger = _snapshot()["chargers"][0]
    chargers = [
        ChargerState(
            fixture_charger["charger_id"],
            fixture_charger["port_count"],
            tuple(fixture_charger["occupied_vehicle_ids"]),
        )
    ]

    chargers = reserve_port(chargers, fixture_charger["charger_id"], "shuttle-01")
    chargers = release_port(chargers, fixture_charger["charger_id"], "shuttle-01")
    chargers = reserve_port(chargers, fixture_charger["charger_id"], "shuttle-02")

    assert chargers[0].occupied_vehicle_ids == ("shuttle-02",)
