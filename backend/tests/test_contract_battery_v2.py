"""
Contract tests for the v2 battery additions: depots, energy forecasts and
charge options.

Author: Choo Jian Wei
Date: 23/10/2026
Last Modified: 26/10/2026

Description:
    Covers the optional Depot, EnergyForecast and ChargeOption parts of
    FullSnapshot (contracts/v2/models.py) that the native battery screen
    (NAT-M2-06) reads: backward compatibility with older 2.x messages,
    per-object rules, snapshot-wide reference checks, and a cross-check
    that the shared fixtures' forecast and charge option numbers equal what
    the backend energy module (backend/src/energy) calculates, so the
    fixtures cannot drift from the real maths.

"""

import copy
import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from backend.src.energy.forecast import loop_energy_kwh, loops_remaining, reserve_kwh, reserve_margin_kwh
from backend.src.energy.model import ENERGY_R1, auxiliary_energy_kwh, charge_battery_kwh, drive_energy_kwh
from contracts.v2.models import ChargeOption, DispatcherCommand, EnergyForecast, parse_message

FIXTURES = Path(__file__).resolve().parents[2] / "contracts" / "v2" / "fixtures"
POSITION = {"x_m": 0.0, "y_m": 0.0, "z_m": 0.0}
FORECAST = {
    "vehicle_id": "shuttle-01", "energy_model_revision": "energy-r1",
    "reserve_kwh": 6.0, "reserve_margin_kwh": 22.0, "loops_remaining": 58,
    "next_depot_id": "depot-1", "next_depot_eta_s": 238.0,
    "reaches_depot_above_reserve": True,
}
CHARGE_NOW = {
    "option_id": "option-1", "vehicle_id": "shuttle-01", "revision": 1,
    "action": "CHARGE_NOW", "charger_id": "charger-1", "validation": "PASS",
    "expires_at_tick": 300, "dependency_revisions": {"shuttle-01": 1, "charger-1": 1},
    "out_of_service_s": 1200.0, "battery_after_kwh": 40.0, "loops_after": 90,
    "assumptions": [],
}
CONTINUE = {**CHARGE_NOW, "option_id": "option-2", "action": "CONTINUE", "charger_id": None,
            "out_of_service_s": 0.0, "battery_after_kwh": 28.0, "loops_after": 58}


def load(name: str) -> dict:
    return json.loads((FIXTURES / f"{name}.json").read_text(encoding="utf-8"))


def snapshot_with(**changes) -> dict:
    value = copy.deepcopy(load("snapshot"))
    if "charge_options" in changes and "charge_option_revisions" not in changes:
        changes["charge_option_revisions"] = {o["option_id"]: o["revision"] for o in changes["charge_options"]}
    value.update(changes)
    return value


def rejection_message(value: dict) -> str:
    with pytest.raises(ValidationError) as error:
        parse_message(value)
    return str(error.value)


# ===================================================================================
# Backward compatibility
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_older_snapshot_without_battery_fields_still_parses
# -----------------------------------------------------------------------------------
Description:
    Depots and energy forecasts are optional. A 2.x snapshot that predates
    them (no keys at all) still parses, with both lists empty, so the
    change stays within contract version 2.

"""
def test_older_snapshot_without_battery_fields_still_parses():
    value = load("snapshot")
    del value["depots"]
    del value["energy_forecasts"]

    snapshot = parse_message(value)

    assert snapshot.depots == []
    assert snapshot.energy_forecasts == []
    assert snapshot.charge_options == []


"""
# -----------------------------------------------------------------------------------
# Function: test_shared_fixture_has_two_chargers_one_depot_and_five_forecasts
# -----------------------------------------------------------------------------------
Description:
    The M2 exercise needs two chargers at a depot and a forecast for every
    shuttle; charger-1 keeps the single port the native tests rely on.

"""
def test_shared_fixture_has_two_chargers_one_depot_and_five_forecasts():
    snapshot = parse_message(load("snapshot"))

    assert [c.charger_id for c in snapshot.chargers] == ["charger-1", "charger-2"]
    assert snapshot.chargers[0].port_count == 1
    assert [(d.depot_id, d.charger_ids) for d in snapshot.depots] == [("depot-1", ["charger-1", "charger-2"])]
    assert sorted(f.vehicle_id for f in snapshot.energy_forecasts) == sorted(v.vehicle_id for v in snapshot.vehicles)


# ===================================================================================
# Depots
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_depot_reference_rules
# -----------------------------------------------------------------------------------
Description:
    A depot must list existing chargers, depot IDs are unique, and a
    charger can only be at one depot.

"""
@pytest.mark.parametrize(
    ("depots", "message"),
    [
        ([{"depot_id": "depot-1", "position": POSITION, "charger_ids": ["charger-9"]}],
         "depot references an unknown charger"),
        ([{"depot_id": "depot-1", "position": POSITION, "charger_ids": ["charger-1"]},
          {"depot_id": "depot-1", "position": POSITION, "charger_ids": ["charger-2"]}],
         "duplicate depot ID"),
        ([{"depot_id": "depot-a", "position": POSITION, "charger_ids": ["charger-1"]},
          {"depot_id": "depot-b", "position": POSITION, "charger_ids": ["charger-1"]}],
         "charger belongs to more than one depot"),
    ],
    ids=["unknown-charger", "duplicate-depot", "charger-in-two-depots"],
)
def test_depot_reference_rules(depots, message):
    assert message in rejection_message(snapshot_with(depots=depots, energy_forecasts=[]))


"""
# -----------------------------------------------------------------------------------
# Function: test_depot_requires_position
# -----------------------------------------------------------------------------------
Description:
    A depot without a position cannot be placed on the map.

"""
def test_depot_requires_position():
    with pytest.raises(ValidationError):
        parse_message(snapshot_with(depots=[{"depot_id": "depot-1", "charger_ids": []}], energy_forecasts=[]))


# ===================================================================================
# Energy forecasts: one forecast on its own
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_forecast_accepts_valid_states
# -----------------------------------------------------------------------------------
Description:
    Below-reserve (negative margin), unavailable loops (None), an unknown
    reach flag, no depot at all, and a shuttle at the depot right now (ETA
    0) are all valid forecasts. is_estimate defaults to True.

"""
@pytest.mark.parametrize(
    "changes",
    [
        {},
        {"reserve_margin_kwh": -2.0},
        {"loops_remaining": None},
        {"reaches_depot_above_reserve": None},
        {"next_depot_id": None, "next_depot_eta_s": None, "reaches_depot_above_reserve": None},
        {"next_depot_eta_s": 0.0},
    ],
    ids=["valid", "below-reserve", "loops-unavailable", "reach-unknown", "no-depot", "at-depot-now"],
)
def test_forecast_accepts_valid_states(changes):
    forecast = EnergyForecast(**{**FORECAST, **changes})

    assert forecast.is_estimate is True


"""
# -----------------------------------------------------------------------------------
# Function: test_forecast_rejects_invalid_values
# -----------------------------------------------------------------------------------
Description:
    NaN or infinity (which would poison the battery screen), negative
    reserve or loops, a non-estimate flag, and depot fields that do not
    belong together are rejected.

"""
@pytest.mark.parametrize(
    "changes",
    [
        {"reserve_margin_kwh": float("nan")},
        {"reserve_kwh": float("inf")},
        {"next_depot_eta_s": float("nan")},
        {"reserve_kwh": -1.0},
        {"loops_remaining": -1},
        {"is_estimate": False},
        {"next_depot_eta_s": None},
        {"next_depot_id": None, "reaches_depot_above_reserve": None},
        {"next_depot_id": None, "next_depot_eta_s": None},
    ],
    ids=["nan-margin", "inf-reserve", "nan-eta", "negative-reserve", "negative-loops", "not-estimate",
         "depot-without-eta", "eta-without-depot", "reach-flag-without-depot"],
)
def test_forecast_rejects_invalid_values(changes):
    with pytest.raises(ValidationError):
        EnergyForecast(**{**FORECAST, **changes})


# ===================================================================================
# Energy forecasts: snapshot-wide references
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_forecast_reference_rules
# -----------------------------------------------------------------------------------
Description:
    A forecast must be for a known shuttle, at most one per shuttle, made
    with the scenario's energy model, and point at a known depot.

"""
@pytest.mark.parametrize(
    ("forecasts", "message"),
    [
        ([FORECAST, FORECAST], "more than one energy forecast for a vehicle"),
        ([{**FORECAST, "vehicle_id": "shuttle-99"}], "unknown"),
        ([{**FORECAST, "energy_model_revision": "energy-r2"}], "different energy model"),
        ([{**FORECAST, "next_depot_id": "depot-9"}], "unknown depot"),
    ],
    ids=["two-for-one-shuttle", "unknown-shuttle", "wrong-energy-model", "unknown-depot"],
)
def test_forecast_reference_rules(forecasts, message):
    assert message in rejection_message(snapshot_with(energy_forecasts=forecasts))


# ===================================================================================
# Fixture numbers equal the backend energy module
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_fixture_forecasts_match_energy_module
# -----------------------------------------------------------------------------------
Description:
    Recalculates every fixture forecast with backend/src/energy using the
    fixture's own battery, capacity and route, so a change to the energy
    maths or to the fixture values cannot go unnoticed.

"""
def test_fixture_forecasts_match_energy_module():
    snapshot = parse_message(load("snapshot"))
    assert snapshot.scenario.energy_model_revision == ENERGY_R1.revision
    routes = {route.route_id: route for route in snapshot.routes}
    vehicles = {vehicle.vehicle_id: vehicle for vehicle in snapshot.vehicles}

    for forecast in snapshot.energy_forecasts:
        vehicle = vehicles[forecast.vehicle_id]
        route = routes[vehicle.route_id]
        loop = loop_energy_kwh(ENERGY_R1, route.distance_m, route.estimated_duration_s)

        assert forecast.reserve_kwh == pytest.approx(reserve_kwh(ENERGY_R1, vehicle.battery_capacity_kwh))
        assert forecast.reserve_margin_kwh == pytest.approx(
            reserve_margin_kwh(ENERGY_R1, vehicle.battery_kwh, vehicle.battery_capacity_kwh))
        assert forecast.loops_remaining == loops_remaining(
            ENERGY_R1, vehicle.battery_kwh, vehicle.battery_capacity_kwh, loop)


"""
# -----------------------------------------------------------------------------------
# Function: test_fixture_depot_eta_and_reach_are_consistent
# -----------------------------------------------------------------------------------
Description:
    The fixture's depot estimate assumes each shuttle drives the rest of
    its loop (route distance minus its x position) at its current speed.
    The reach flag must agree with the energy needed for that trip versus
    the margin above reserve.

"""
def test_fixture_depot_eta_and_reach_are_consistent():
    snapshot = parse_message(load("snapshot"))
    routes = {route.route_id: route for route in snapshot.routes}
    vehicles = {vehicle.vehicle_id: vehicle for vehicle in snapshot.vehicles}

    for forecast in snapshot.energy_forecasts:
        vehicle = vehicles[forecast.vehicle_id]
        remaining_m = routes[vehicle.route_id].distance_m - vehicle.position.x_m
        eta_s = remaining_m / vehicle.speed_mps
        trip_kwh = drive_energy_kwh(ENERGY_R1, remaining_m) + auxiliary_energy_kwh(ENERGY_R1, eta_s)

        assert forecast.next_depot_eta_s == pytest.approx(eta_s)
        assert forecast.reaches_depot_above_reserve is (trip_kwh <= forecast.reserve_margin_kwh)


# ===================================================================================
# Charge options: per-option rules
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_charge_option_accepts_valid_states
# -----------------------------------------------------------------------------------
Description:
    Charging now at a charger, continuing in service, and returning to the
    depot (with or without a known charger) are valid options, as are
    unavailable loops and every validation result. is_estimate defaults to
    True.

"""
@pytest.mark.parametrize(
    "option",
    [
        CHARGE_NOW,
        CONTINUE,
        {**CHARGE_NOW, "action": "RETURN_TO_DEPOT"},
        {**CHARGE_NOW, "action": "RETURN_TO_DEPOT", "charger_id": None},
        {**CHARGE_NOW, "loops_after": None},
        {**CHARGE_NOW, "validation": "WARN"},
        {**CHARGE_NOW, "validation": "FAIL"},
        {**CHARGE_NOW, "validation": "UNKNOWN"},
    ],
    ids=["charge-now", "continue", "return-to-depot", "return-without-charger", "loops-unavailable",
         "warn", "fail", "unknown"],
)
def test_charge_option_accepts_valid_states(option):
    assert ChargeOption(**option).is_estimate is True


"""
# -----------------------------------------------------------------------------------
# Function: test_charge_option_rejects_invalid_values
# -----------------------------------------------------------------------------------
Description:
    An unknown action or validation, NaN or negative estimates, a
    non-estimate flag, CHARGE_NOW without a charger, and CONTINUE that uses
    a charger or takes the shuttle out of service are rejected.

"""
@pytest.mark.parametrize(
    "option",
    [
        {**CHARGE_NOW, "action": "TELEPORT"},
        {**CHARGE_NOW, "validation": "MAYBE"},
        {**CHARGE_NOW, "battery_after_kwh": float("nan")},
        {**CHARGE_NOW, "out_of_service_s": float("inf")},
        {**CHARGE_NOW, "battery_after_kwh": -1.0},
        {**CHARGE_NOW, "out_of_service_s": -1.0},
        {**CHARGE_NOW, "loops_after": -1},
        {**CHARGE_NOW, "is_estimate": False},
        {**CHARGE_NOW, "charger_id": None},
        {**CONTINUE, "charger_id": "charger-1"},
        {**CONTINUE, "out_of_service_s": 60.0},
    ],
    ids=["unknown-action", "unknown-validation", "nan-battery", "inf-out-of-service", "negative-battery",
         "negative-out-of-service", "negative-loops", "not-estimate", "charge-now-without-charger",
         "continue-with-charger", "continue-out-of-service"],
)
def test_charge_option_rejects_invalid_values(option):
    with pytest.raises(ValidationError):
        ChargeOption(**option)


# ===================================================================================
# Charge options: snapshot-wide references
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_charge_option_reference_rules
# -----------------------------------------------------------------------------------
Description:
    Option IDs are unique, an option is for a known shuttle and a known
    charger, it cannot promise more charge than the battery holds, and an
    option without a charger (CONTINUE, or RETURN_TO_DEPOT with no charger
    yet) cannot end with more battery than the shuttle has now.

"""
@pytest.mark.parametrize(
    ("options", "message"),
    [
        ([CHARGE_NOW, CHARGE_NOW], "duplicate charge option ID"),
        ([{**CHARGE_NOW, "vehicle_id": "shuttle-99"}], "charge option for an unknown vehicle"),
        ([{**CHARGE_NOW, "charger_id": "charger-9"}], "unknown charger"),
        ([{**CHARGE_NOW, "battery_after_kwh": 40.5}], "exceeds the vehicle's capacity"),
        ([{**CONTINUE, "battery_after_kwh": 40.0}], "without a charger cannot gain battery"),
        ([{**CHARGE_NOW, "action": "RETURN_TO_DEPOT", "charger_id": None}],
         "without a charger cannot gain battery"),
    ],
    ids=["duplicate-id", "unknown-shuttle", "unknown-charger", "above-capacity",
         "continue-gains-battery", "return-without-charger-gains-battery"],
)
def test_charge_option_reference_rules(options, message):
    assert message in rejection_message(snapshot_with(charge_options=options))


"""
# -----------------------------------------------------------------------------------
# Function: test_charge_options_for_several_shuttles_are_accepted
# -----------------------------------------------------------------------------------
Description:
    One shuttle can have several options and several shuttles can have
    options at once; a full battery (exactly at capacity) is allowed.

"""
def test_charge_options_for_several_shuttles_are_accepted():
    options = [CHARGE_NOW, CONTINUE, {**CHARGE_NOW, "option_id": "option-3", "vehicle_id": "shuttle-02"}]

    snapshot = parse_message(snapshot_with(charge_options=options))

    assert [option.option_id for option in snapshot.charge_options] == ["option-1", "option-2", "option-3"]


# ===================================================================================
# Low-reserve fixture
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_low_reserve_fixture_shape
# -----------------------------------------------------------------------------------
Description:
    The low-reserve fixture is the normal snapshot with only shuttle-05
    near its reserve and given a CONTINUE and a CHARGE_NOW option, so the
    battery screen has one shuttle that needs a decision.

"""
def test_low_reserve_fixture_shape():
    normal = parse_message(load("snapshot"))
    low = parse_message(load("low_reserve_snapshot"))

    assert low.snapshot_id != normal.snapshot_id
    changed = [v.vehicle_id for v, n in zip(low.vehicles, normal.vehicles) if v.battery_kwh != n.battery_kwh]
    assert changed == ["shuttle-05"]
    assert {(o.vehicle_id, o.action) for o in low.charge_options} == {
        ("shuttle-05", "CONTINUE"), ("shuttle-05", "CHARGE_NOW")}
    assert normal.charge_options == []


"""
# -----------------------------------------------------------------------------------
# Function: test_low_reserve_fixture_matches_energy_module
# -----------------------------------------------------------------------------------
Description:
    Recalculates the low-reserve shuttle's forecast and both charge
    options with backend/src/energy: CONTINUE keeps the current battery
    and loops, CHARGE_NOW adds what its charger delivers while the shuttle
    is out of service.

"""
def test_low_reserve_fixture_matches_energy_module():
    snapshot = parse_message(load("low_reserve_snapshot"))
    route = snapshot.routes[0]
    loop = loop_energy_kwh(ENERGY_R1, route.distance_m, route.estimated_duration_s)
    vehicles = {vehicle.vehicle_id: vehicle for vehicle in snapshot.vehicles}
    chargers = {charger.charger_id: charger for charger in snapshot.chargers}
    forecasts = {forecast.vehicle_id: forecast for forecast in snapshot.energy_forecasts}

    for option in snapshot.charge_options:
        vehicle = vehicles[option.vehicle_id]
        forecast = forecasts[option.vehicle_id]
        assert forecast.reserve_margin_kwh == pytest.approx(
            reserve_margin_kwh(ENERGY_R1, vehicle.battery_kwh, vehicle.battery_capacity_kwh))
        assert forecast.loops_remaining == loops_remaining(
            ENERGY_R1, vehicle.battery_kwh, vehicle.battery_capacity_kwh, loop)

        if option.action == "CONTINUE":
            expected_kwh = vehicle.battery_kwh
        else:
            expected_kwh = charge_battery_kwh(ENERGY_R1, vehicle.battery_kwh, vehicle.battery_capacity_kwh,
                                              chargers[option.charger_id].power_kw, option.out_of_service_s)
        assert option.battery_after_kwh == pytest.approx(expected_kwh)
        assert option.loops_after == loops_remaining(
            ENERGY_R1, expected_kwh, vehicle.battery_capacity_kwh, loop)


"""
# -----------------------------------------------------------------------------------
# Function: test_charge_option_revision_history
# -----------------------------------------------------------------------------------
Description:
    charge_option_revisions is optional and may keep entries for withdrawn
    offers. A 2.x snapshot that has active offers but no map (written before
    the map existed) still parses; only an entry that disagrees with its
    active offer is rejected.

"""
def test_charge_option_revision_history():
    assert parse_message(load("snapshot")).charge_option_revisions == {}
    withdrawn = snapshot_with(charge_option_revisions={"charge-option-shuttle-05-continue": 4})
    assert parse_message(withdrawn).charge_option_revisions == {"charge-option-shuttle-05-continue": 4}
    older = load("low_reserve_snapshot")
    del older["charge_option_revisions"]
    assert len(parse_message(older).charge_options) == 2

    assert "last issued revision" in rejection_message(
        snapshot_with(charge_options=[CHARGE_NOW], charge_option_revisions={"option-1": 2}))


"""
# -----------------------------------------------------------------------------------
# Function: test_min_in_service_vehicles_rules
# -----------------------------------------------------------------------------------
Description:
    The fleet-coverage estimate is optional (absent means unknown, so older
    2.x options still parse), never negative, and never more than the
    number of shuttles in the snapshot.

"""
def test_min_in_service_vehicles_rules():
    assert ChargeOption(**CHARGE_NOW).min_in_service_vehicles is None
    assert ChargeOption(**{**CHARGE_NOW, "min_in_service_vehicles": 0}).min_in_service_vehicles == 0
    with pytest.raises(ValidationError):
        ChargeOption(**{**CHARGE_NOW, "min_in_service_vehicles": -1})

    fleet = len(load("snapshot")["vehicles"])
    parse_message(snapshot_with(charge_options=[{**CHARGE_NOW, "min_in_service_vehicles": fleet}]))
    assert "exceeds the fleet size" in rejection_message(
        snapshot_with(charge_options=[{**CHARGE_NOW, "min_in_service_vehicles": fleet + 1}]))


"""
# -----------------------------------------------------------------------------------
# Function: test_offered_at_tick_rules
# -----------------------------------------------------------------------------------
Description:
    When the decision was first offered is optional (absent in older 2.x
    options), and an option cannot be offered after it expires.

"""
def test_offered_at_tick_rules():
    assert ChargeOption(**CHARGE_NOW).offered_at_tick is None
    assert ChargeOption(**{**CHARGE_NOW, "offered_at_tick": 300}).offered_at_tick == 300
    with pytest.raises(ValidationError, match="offered after it expires"):
        ChargeOption(**{**CHARGE_NOW, "offered_at_tick": 301})


"""
# -----------------------------------------------------------------------------------
# Function: test_charge_decision_deferral_rules
# -----------------------------------------------------------------------------------
Description:
    The CONTINUE deferral map is optional (absent in older 2.x snapshots),
    names known shuttles only, and never holds a negative loop count. A zero
    entry (a shuttle at zero loops can never fall further) means "not
    deferred" and is dropped on parse, so older saved snapshots still load.

"""
def test_charge_decision_deferral_rules():
    assert parse_message(load("snapshot")).charge_decision_deferrals == {}
    assert parse_message(snapshot_with(charge_decision_deferrals={"shuttle-05": 1})).charge_decision_deferrals == {
        "shuttle-05": 1}
    assert "deferral for an unknown vehicle" in rejection_message(
        snapshot_with(charge_decision_deferrals={"shuttle-99": 2}))
    assert parse_message(snapshot_with(charge_decision_deferrals={"shuttle-05": 0, "shuttle-04": 2})
                         ).charge_decision_deferrals == {"shuttle-04": 2}
    with pytest.raises(ValidationError):
        parse_message(snapshot_with(charge_decision_deferrals={"shuttle-05": -1}))


"""
# -----------------------------------------------------------------------------------
# Function: test_continue_service_command_needs_a_reason
# -----------------------------------------------------------------------------------
Description:
    CONTINUE_SERVICE is a decision command targeting a VEHICLE, so like
    RESERVE_CHARGER it must carry a reason.

"""
def test_continue_service_command_needs_a_reason():
    command = {"contract_version": "2.0", "command_id": "c-1", "idempotency_key": "k-1",
               "session_id": "session-001", "command_type": "CONTINUE_SERVICE", "expected_epoch": 1,
               "expected_revision": 1, "target_type": "VEHICLE", "target_id": "shuttle-05",
               "payload": {"option_id": "charge-option-shuttle-05-continue", "option_revision": 1},
               "reason": "Peak is nearly over"}

    assert DispatcherCommand.model_validate(command).target_type == "VEHICLE"
    with pytest.raises(ValidationError, match="requires reason"):
        DispatcherCommand.model_validate({**command, "reason": None})
