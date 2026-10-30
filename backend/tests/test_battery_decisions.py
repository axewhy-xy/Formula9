"""
Tests for the battery decision functions (backend/src/energy/decisions.py).

Author: Choo Jian Wei
Date: 24/10/2026
Last Modified: 26/10/2026

Description:
    Part of BE-M2-05 (battery decision). Checks that energy_forecasts and
    charge_options reproduce the shared v2 fixtures exactly, handle the
    edge cases a live session will hit (no depot, stopped or unrouted
    shuttles, full chargers, several low shuttles), and always produce
    output the v2 contract accepts.

"""

import json
from pathlib import Path

import pytest

from backend.src.energy.decisions import (
    LOW_LOOPS,
    OPTION_LIFETIME_TICKS,
    charge_options,
    energy_forecasts,
    forecast_for_vehicle,
    vehicle_option_ids,
    with_battery_decisions,
)
from backend.src.energy.model import ENERGY_R1
from contracts.v2.models import FullSnapshot

FIXTURES = Path(__file__).resolve().parents[2] / "contracts" / "v2" / "fixtures"


def load(name: str) -> FullSnapshot:
    return FullSnapshot.model_validate(json.loads((FIXTURES / f"{name}.json").read_text(encoding="utf-8")))


def dumps(items) -> list[dict]:
    return [item.model_dump() for item in items]


def with_results(snapshot: FullSnapshot) -> FullSnapshot:
    """Fills the battery lists the way the actor will, then re-validates the snapshot."""
    return FullSnapshot.model_validate(with_battery_decisions(ENERGY_R1, snapshot).model_dump(mode="json"))


# ===================================================================================
# Forecasts
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_forecasts_reproduce_fixtures
# -----------------------------------------------------------------------------------
Description:
    The functions are the source of the fixture numbers: computing the
    forecasts from each fixture's own shuttles gives exactly its list.

"""
@pytest.mark.parametrize("name", ["snapshot", "low_reserve_snapshot"])
def test_forecasts_reproduce_fixtures(name):
    snapshot = load(name)

    assert dumps(energy_forecasts(ENERGY_R1, snapshot)) == dumps(snapshot.energy_forecasts)


"""
# -----------------------------------------------------------------------------------
# Function: test_forecast_without_depot_or_when_stopped_has_no_depot_fields
# -----------------------------------------------------------------------------------
Description:
    With no depot, or a stopped shuttle (no ETA can be given), all three
    depot fields are None, while reserve and loops are still forecast.

"""
@pytest.mark.parametrize("stopped, with_depot", [(False, False), (True, True)], ids=["no-depot", "stopped"])
def test_forecast_without_depot_or_when_stopped_has_no_depot_fields(stopped, with_depot):
    snapshot = load("snapshot")
    vehicle = snapshot.vehicles[0].model_copy(update={"speed_mps": 0.0} if stopped else {})

    forecast = forecast_for_vehicle(ENERGY_R1, vehicle, snapshot.routes[0],
                                    snapshot.depots[0] if with_depot else None)

    assert (forecast.next_depot_id, forecast.next_depot_eta_s, forecast.reaches_depot_above_reserve) == (None, None, None)
    assert forecast.loops_remaining == 58
    assert forecast.reserve_margin_kwh == pytest.approx(22.0)


"""
# -----------------------------------------------------------------------------------
# Function: test_forecast_past_route_end_clamps_eta_to_zero
# -----------------------------------------------------------------------------------
Description:
    A position beyond the route length must not give a negative ETA (which
    the contract rejects); the remaining distance is clamped to 0.

"""
def test_forecast_past_route_end_clamps_eta_to_zero():
    snapshot = load("snapshot")
    vehicle = snapshot.vehicles[0]
    vehicle = vehicle.model_copy(update={"position": vehicle.position.model_copy(update={"x_m": 1500.0})})

    forecast = forecast_for_vehicle(ENERGY_R1, vehicle, snapshot.routes[0], snapshot.depots[0])

    assert forecast.next_depot_eta_s == 0.0
    assert forecast.reaches_depot_above_reserve is True


"""
# -----------------------------------------------------------------------------------
# Function: test_trip_using_exactly_the_margin_still_reaches_the_depot
# -----------------------------------------------------------------------------------
Description:
    Arriving exactly at reserve counts as reaching the depot above reserve
    (the comparison is <=): a shuttle at the depot with zero margin needs
    zero energy and still reaches it.

"""
def test_trip_using_exactly_the_margin_still_reaches_the_depot():
    snapshot = load("snapshot")
    vehicle = snapshot.vehicles[0]
    vehicle = vehicle.model_copy(update={"battery_kwh": 6.0,
                                         "position": vehicle.position.model_copy(update={"x_m": 1200.0})})

    forecast = forecast_for_vehicle(ENERGY_R1, vehicle, snapshot.routes[0], snapshot.depots[0])

    assert forecast.reserve_margin_kwh == 0.0
    assert forecast.reaches_depot_above_reserve is True


"""
# -----------------------------------------------------------------------------------
# Function: test_forecast_at_or_below_reserve
# -----------------------------------------------------------------------------------
Description:
    Exactly at reserve the shuttle has no loops left and cannot reach the
    depot above reserve; below reserve the margin is negative.

"""
def test_forecast_at_or_below_reserve():
    snapshot = load("snapshot")
    at_reserve = snapshot.vehicles[0].model_copy(update={"battery_kwh": 6.0})
    below = snapshot.vehicles[0].model_copy(update={"battery_kwh": 4.0})

    forecast = forecast_for_vehicle(ENERGY_R1, at_reserve, snapshot.routes[0], snapshot.depots[0])
    assert forecast.loops_remaining == 0
    assert forecast.reaches_depot_above_reserve is False
    assert forecast_for_vehicle(ENERGY_R1, below, snapshot.routes[0], None).reserve_margin_kwh == pytest.approx(-2.0)


"""
# -----------------------------------------------------------------------------------
# Function: test_unrouted_shuttles_and_missing_depots
# -----------------------------------------------------------------------------------
Description:
    A shuttle without a route is skipped (loops need a route), and a
    snapshot without depots still forecasts every routed shuttle.

"""
def test_unrouted_shuttles_and_missing_depots():
    snapshot = load("snapshot")
    snapshot.vehicles[2].route_id = None
    snapshot.vehicles[2].route_revision = None
    snapshot.depots = []

    forecasts = energy_forecasts(ENERGY_R1, snapshot)

    assert [f.vehicle_id for f in forecasts] == ["shuttle-01", "shuttle-02", "shuttle-04", "shuttle-05"]
    assert {f.next_depot_id for f in forecasts} == {None}


# ===================================================================================
# Charge options
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_charge_options_reproduce_fixtures
# -----------------------------------------------------------------------------------
Description:
    No shuttle in the normal snapshot is low, so it has no options; the
    low-reserve fixture's CONTINUE and CHARGE_NOW options are reproduced
    exactly, field for field.

"""
@pytest.mark.parametrize("name", ["snapshot", "low_reserve_snapshot"])
def test_charge_options_reproduce_fixtures(name):
    snapshot = load(name)

    assert dumps(charge_options(ENERGY_R1, snapshot)) == dumps(snapshot.charge_options)


"""
# -----------------------------------------------------------------------------------
# Function: test_low_loops_threshold_is_inclusive
# -----------------------------------------------------------------------------------
Description:
    A shuttle with exactly LOW_LOOPS loops gets a decision; one more loop
    and it does not.

"""
@pytest.mark.parametrize(("battery_kwh", "expected_loops", "gets_options"),
                         [(7.2, LOW_LOOPS, True), (7.6, LOW_LOOPS + 1, False)],
                         ids=["at-threshold", "above-threshold"])
def test_low_loops_threshold_is_inclusive(battery_kwh, expected_loops, gets_options):
    snapshot = load("snapshot")
    snapshot.vehicles[0].battery_kwh = battery_kwh

    assert energy_forecasts(ENERGY_R1, snapshot)[0].loops_remaining == expected_loops
    assert bool(charge_options(ENERGY_R1, snapshot)) is gets_options


"""
# -----------------------------------------------------------------------------------
# Function: test_charge_now_uses_first_free_charger
# -----------------------------------------------------------------------------------
Description:
    CHARGE_NOW goes to the first charger by ID with a free port; when every
    charger is full only CONTINUE is offered.

"""
@pytest.mark.parametrize(("occupied", "expected"), [
    ({}, ["CONTINUE", "CHARGE_NOW:charger-1"]),
    ({"charger-1": ["shuttle-01"]}, ["CONTINUE", "CHARGE_NOW:charger-2"]),
    ({"charger-1": ["shuttle-01"], "charger-2": ["shuttle-02"]}, ["CONTINUE"]),
], ids=["both-free", "charger-1-busy", "all-busy"])
def test_charge_now_uses_first_free_charger(occupied, expected):
    snapshot = load("low_reserve_snapshot")
    for charger in snapshot.chargers:
        charger.occupied_vehicle_ids = occupied.get(charger.charger_id, [])

    options = charge_options(ENERGY_R1, snapshot)

    assert [o.action + (f":{o.charger_id}" if o.charger_id else "") for o in options] == expected


"""
# -----------------------------------------------------------------------------------
# Function: test_charge_option_fields_follow_inputs
# -----------------------------------------------------------------------------------
Description:
    Expiry follows the snapshot tick, dependencies pin the shuttle and
    charger revisions, and a longer charge is capped at capacity with the
    assumption naming the right duration.

"""
def test_charge_option_fields_follow_inputs():
    snapshot = load("low_reserve_snapshot")
    snapshot.tick = 1000
    snapshot.vehicles[4].aggregate_revision = 7

    continue_option, charge_now = charge_options(ENERGY_R1, snapshot, charge_s=3 * 3600.0)

    assert continue_option.expires_at_tick == charge_now.expires_at_tick == 1000 + OPTION_LIFETIME_TICKS
    assert continue_option.dependency_revisions == {"shuttle-05": 7}
    assert charge_now.dependency_revisions == {"shuttle-05": 7, "charger-1": 1}
    assert charge_now.battery_after_kwh == pytest.approx(40.0)
    assert charge_now.out_of_service_s == 3 * 3600.0
    assert charge_now.assumptions == ["charger-1 stays free for 180 minutes"]


"""
# -----------------------------------------------------------------------------------
# Function: test_several_low_shuttles_get_unique_options
# -----------------------------------------------------------------------------------
Description:
    Two low shuttles each get their own CONTINUE and CHARGE_NOW options
    with IDs that include the shuttle, and the snapshot holding them passes
    every contract rule (no duplicate option IDs).

"""
def test_several_low_shuttles_get_unique_options():
    snapshot = load("low_reserve_snapshot")
    snapshot.vehicles[3].battery_kwh = 7.0

    result = with_results(snapshot)

    assert [o.option_id for o in result.charge_options] == [
        "charge-option-shuttle-04-continue", "charge-option-shuttle-04-charger-1",
        "charge-option-shuttle-05-continue", "charge-option-shuttle-05-charger-1"]


"""
# -----------------------------------------------------------------------------------
# Function: test_results_pass_the_contract
# -----------------------------------------------------------------------------------
Description:
    Across normal, low, at-reserve, below-reserve, stopped and full-charger
    states, the computed lists always form a snapshot the contract accepts.

"""
@pytest.mark.parametrize("change", [
    lambda s: None,
    lambda s: setattr(s.vehicles[4], "battery_kwh", 6.0),
    lambda s: setattr(s.vehicles[4], "battery_kwh", 1.0),
    lambda s: setattr(s.vehicles[4], "speed_mps", 0.0),
    lambda s: [setattr(c, "occupied_vehicle_ids", [v.vehicle_id]) or setattr(v, "service_state", "CHARGING")
               for c, v in zip(s.chargers, s.vehicles)],
], ids=["low-reserve", "at-reserve", "below-reserve", "stopped", "chargers-full"])
def test_results_pass_the_contract(change):
    snapshot = load("low_reserve_snapshot")
    change(snapshot)

    result = with_results(snapshot)

    assert len(result.energy_forecasts) == 5
    assert all(o.is_estimate for o in result.charge_options)


# ===================================================================================
# Review fixes (PR #52): eligibility and offer versions
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_shuttles_that_cannot_head_to_the_depot_get_no_choices
# -----------------------------------------------------------------------------------
Description:
    A low shuttle already returning to the depot, charging, blocked, or on
    a charger port (for example right after reserving one) is offered no
    CONTINUE or CHARGE_NOW choice.

"""
@pytest.mark.parametrize("change", [
    lambda s: setattr(s.vehicles[4], "service_state", "RETURNING_TO_DEPOT"),
    lambda s: setattr(s.vehicles[4], "service_state", "BLOCKED"),
    lambda s: setattr(s.chargers[0], "occupied_vehicle_ids", ["shuttle-05"]),
    lambda s: (setattr(s.vehicles[4], "service_state", "CHARGING"),
               setattr(s.chargers[0], "occupied_vehicle_ids", ["shuttle-05"])),
], ids=["returning", "blocked", "on-port", "charging"])
def test_shuttles_that_cannot_head_to_the_depot_get_no_choices(change):
    snapshot = load("low_reserve_snapshot")
    change(snapshot)

    assert charge_options(ENERGY_R1, snapshot) == []


"""
# -----------------------------------------------------------------------------------
# Function: test_unchanged_offer_stays_frozen_while_unexpired
# -----------------------------------------------------------------------------------
Description:
    While an offer is unexpired and its shuttle and charger revisions are
    unchanged, regenerating returns it exactly as offered (same revision,
    expiry and numbers), even after the clock moves on.

"""
def test_unchanged_offer_stays_frozen_while_unexpired():
    snapshot = load("low_reserve_snapshot")
    offered = dumps(snapshot.charge_options)
    snapshot.tick = 290

    assert dumps(charge_options(ENERGY_R1, snapshot)) == offered


"""
# -----------------------------------------------------------------------------------
# Function: test_offer_is_revised_when_it_changes_or_expires
# -----------------------------------------------------------------------------------
Description:
    A changed shuttle or charger revision, or an expired offer, reissues the
    option with the next revision; an offer without a charger is also
    reissued once the draining battery falls below what it promised.

"""
@pytest.mark.parametrize(("change", "revised"), [
    (lambda s: setattr(s.vehicles[4], "aggregate_revision", 2), {"charge-option-shuttle-05-continue",
                                                                  "charge-option-shuttle-05-charger-1"}),
    (lambda s: setattr(s.chargers[0], "aggregate_revision", 2), {"charge-option-shuttle-05-charger-1"}),
    (lambda s: setattr(s, "tick", 301), {"charge-option-shuttle-05-continue",
                                         "charge-option-shuttle-05-charger-1"}),
    (lambda s: setattr(s.vehicles[4], "battery_kwh", 6.9), {"charge-option-shuttle-05-continue"}),
], ids=["shuttle-changed", "charger-changed", "expired", "battery-drained"])
def test_offer_is_revised_when_it_changes_or_expires(change, revised):
    snapshot = load("low_reserve_snapshot")
    change(snapshot)

    options = {o.option_id: o for o in charge_options(ENERGY_R1, snapshot)}

    assert {option_id for option_id, o in options.items() if o.revision == 2} == revised
    assert all(o.revision == 1 for option_id, o in options.items() if option_id not in revised)


"""
# -----------------------------------------------------------------------------------
# Function: test_withdrawn_offer_is_recreated_with_a_new_revision
# -----------------------------------------------------------------------------------
Description:
    A blocked shuttle's offers are withdrawn but their revisions stay in the
    history; when it returns to service the offers are re-created with the
    next revision instead of revision 1 again.

"""
def test_withdrawn_offer_is_recreated_with_a_new_revision():
    snapshot = with_battery_decisions(ENERGY_R1, load("low_reserve_snapshot"))
    snapshot.vehicles[4].service_state = "BLOCKED"
    blocked = with_battery_decisions(ENERGY_R1, snapshot)
    assert blocked.charge_options == []
    assert blocked.charge_option_revisions == {"charge-option-shuttle-05-continue": 1,
                                               "charge-option-shuttle-05-charger-1": 1}

    blocked.vehicles[4].service_state = "IN_SERVICE"
    back = with_results(blocked)

    assert {o.option_id: o.revision for o in back.charge_options} == {
        "charge-option-shuttle-05-continue": 2, "charge-option-shuttle-05-charger-1": 2}
    assert back.charge_option_revisions == {o.option_id: 2 for o in back.charge_options}


"""
# -----------------------------------------------------------------------------------
# Function: test_with_battery_decisions_does_not_change_its_input
# -----------------------------------------------------------------------------------
Description:
    The actor's helper returns a filled copy and leaves the snapshot it was
    given untouched.

"""
def test_with_battery_decisions_does_not_change_its_input():
    snapshot = load("low_reserve_snapshot")
    snapshot.vehicles[3].battery_kwh = 7.0
    before = snapshot.model_dump()

    result = with_battery_decisions(ENERGY_R1, snapshot)

    assert snapshot.model_dump() == before
    assert len(result.charge_options) == 4


"""
# -----------------------------------------------------------------------------------
# Function: test_older_snapshot_without_history_is_upgraded
# -----------------------------------------------------------------------------------
Description:
    A 2.x snapshot with active offers but no revision map keeps its offers
    unchanged, and with_battery_decisions fills the map from them so later
    withdrawals are tracked.

"""
def test_older_snapshot_without_history_is_upgraded():
    older = load("low_reserve_snapshot")
    older.charge_option_revisions = {}

    upgraded = with_results(older)

    assert dumps(upgraded.charge_options) == dumps(older.charge_options)
    assert upgraded.charge_option_revisions == {o.option_id: 1 for o in older.charge_options}


# ===================================================================================
# Fleet coverage (WEB-M2-05 minimum active-fleet consequence)
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_options_report_shuttles_left_in_service
# -----------------------------------------------------------------------------------
Description:
    With all five shuttles serving, CONTINUE keeps five in service and
    CHARGE_NOW takes the low shuttle out, leaving four.

"""
def test_options_report_shuttles_left_in_service():
    options = {o.action: o for o in charge_options(ENERGY_R1, load("low_reserve_snapshot"))}

    assert options["CONTINUE"].min_in_service_vehicles == 5
    assert options["CHARGE_NOW"].min_in_service_vehicles == 4


"""
# -----------------------------------------------------------------------------------
# Function: test_only_serving_states_count
# -----------------------------------------------------------------------------------
Description:
    IN_SERVICE, DWELLING and DETOURING count as serving; standby, blocked,
    returning and charging shuttles do not.

"""
@pytest.mark.parametrize(("state", "serving"), [
    ("IN_SERVICE", True), ("DWELLING", True), ("DETOURING", True),
    ("STANDBY", False), ("BLOCKED", False), ("AWAITING_GUIDANCE", False),
    ("RETURNING_TO_DEPOT", False), ("OUT_OF_SERVICE", False),
])
def test_only_serving_states_count(state, serving):
    snapshot = load("low_reserve_snapshot")
    snapshot.vehicles[0].service_state = state

    options = {o.action: o for o in charge_options(ENERGY_R1, snapshot)}

    assert options["CONTINUE"].min_in_service_vehicles == (5 if serving else 4)
    assert options["CHARGE_NOW"].min_in_service_vehicles == (4 if serving else 3)


"""
# -----------------------------------------------------------------------------------
# Function: test_offer_is_revised_when_coverage_changes
# -----------------------------------------------------------------------------------
Description:
    A frozen offer whose shuttle and charger are unchanged is still
    reissued when another shuttle leaves service, so the trainee never
    acts on a stale coverage number; the reissued offer reports one fewer.

"""
def test_offer_is_revised_when_coverage_changes():
    snapshot = load("low_reserve_snapshot")
    snapshot.vehicles[0].service_state = "RETURNING_TO_DEPOT"

    options = {o.action: o for o in charge_options(ENERGY_R1, snapshot)}

    assert (options["CONTINUE"].revision, options["CONTINUE"].min_in_service_vehicles) == (2, 4)
    assert (options["CHARGE_NOW"].revision, options["CHARGE_NOW"].min_in_service_vehicles) == (2, 3)


"""
# -----------------------------------------------------------------------------------
# Function: test_coverage_results_pass_the_contract
# -----------------------------------------------------------------------------------
Description:
    Computed coverage always forms a snapshot the contract accepts.

"""
def test_coverage_results_pass_the_contract():
    snapshot = load("low_reserve_snapshot")
    snapshot.vehicles[3].battery_kwh = 7.0

    result = with_results(snapshot)

    assert {o.min_in_service_vehicles for o in result.charge_options} == {5, 4}


"""
# -----------------------------------------------------------------------------------
# Function: test_reissued_offer_keeps_when_the_decision_was_first_offered
# -----------------------------------------------------------------------------------
Description:
    Reissuing an expired offer gives it a new revision and expiry but keeps
    offered_at_tick, so a slow decision is timed from the first offer.

"""
def test_reissued_offer_keeps_when_the_decision_was_first_offered():
    snapshot = load("low_reserve_snapshot")
    snapshot.tick = 1600

    options = charge_options(ENERGY_R1, snapshot)

    assert {(o.revision, o.expires_at_tick, o.offered_at_tick) for o in options} == {(2, 1700, 200)}


"""
# -----------------------------------------------------------------------------------
# Function: test_decision_start_is_shared_by_the_shuttles_offers
# -----------------------------------------------------------------------------------
Description:
    An offer that appears later in the same decision (a charger port frees
    up) starts from the shuttle's earliest offer; older offers without
    offered_at_tick fall back to their expiry minus the offer lifetime.

"""
def test_decision_start_is_shared_by_the_shuttles_offers():
    snapshot = load("low_reserve_snapshot")
    snapshot.charge_options = [o.model_copy(update={"offered_at_tick": None})
                               for o in snapshot.charge_options if o.action == "CONTINUE"]
    snapshot.tick = 250

    options = {o.action: o for o in charge_options(ENERGY_R1, snapshot)}

    assert options["CHARGE_NOW"].offered_at_tick == 200
    assert options["CONTINUE"].offered_at_tick is None  # still frozen as offered


"""
# -----------------------------------------------------------------------------------
# Function: test_new_decision_starts_when_offered
# -----------------------------------------------------------------------------------
Description:
    Once a shuttle's offers have been withdrawn, a new decision is timed
    from the tick it is offered.

"""
def test_new_decision_starts_when_offered():
    snapshot = load("low_reserve_snapshot")
    snapshot.vehicles[4].service_state = "BLOCKED"
    blocked = with_battery_decisions(ENERGY_R1, snapshot)
    blocked.vehicles[4].service_state = "IN_SERVICE"
    blocked.tick = 500

    assert {o.offered_at_tick for o in with_results(blocked).charge_options} == {500}


"""
# -----------------------------------------------------------------------------------
# Function: test_reissue_uses_the_earliest_start_of_the_shuttles_offers
# -----------------------------------------------------------------------------------
Description:
    When a shuttle's offers started at different ticks, a reissued offer
    takes the earliest one, since that is when the decision began.

"""
def test_reissue_uses_the_earliest_start_of_the_shuttles_offers():
    snapshot = load("low_reserve_snapshot")
    snapshot.charge_options[1].offered_at_tick = 250
    snapshot.chargers[0].aggregate_revision = 2
    snapshot.tick = 260

    options = {o.action: o for o in charge_options(ENERGY_R1, snapshot)}

    assert (options["CHARGE_NOW"].revision, options["CHARGE_NOW"].offered_at_tick) == (2, 200)


"""
# -----------------------------------------------------------------------------------
# Function: test_continue_deferral_waits_for_the_next_loop
# -----------------------------------------------------------------------------------
Description:
    After the operator chose CONTINUE at 2 loops, the shuttle gets no new
    offers while it still has 2 loops; once it falls to 1 loop it is asked
    again and the deferral is dropped.

"""
def test_continue_deferral_waits_for_the_next_loop():
    snapshot = load("low_reserve_snapshot")
    snapshot.charge_options = []
    snapshot.charge_decision_deferrals = {"shuttle-05": 2}

    waiting = with_results(snapshot)
    assert waiting.charge_options == []
    assert waiting.charge_decision_deferrals == {"shuttle-05": 2}

    waiting.vehicles[4].battery_kwh = 6.5  # 1 loop left
    asked = with_results(waiting)
    assert {o.vehicle_id for o in asked.charge_options} == {"shuttle-05"}
    assert asked.charge_decision_deferrals == {}


"""
# -----------------------------------------------------------------------------------
# Function: test_deferral_is_dropped_when_it_no_longer_applies
# -----------------------------------------------------------------------------------
Description:
    A deferral is cleared once the shuttle is no longer low (charged above
    LOW_LOOPS), takes a charger port, or loses its forecast (no route), so
    a later low battery starts a fresh decision.

"""
@pytest.mark.parametrize("change", [
    lambda s: setattr(s.vehicles[4], "battery_kwh", 40.0),
    lambda s: s.chargers[0].occupied_vehicle_ids.append("shuttle-05"),
    lambda s: setattr(s.vehicles[4], "route_id", None),
], ids=["recharged", "on-charger", "no-route"])
def test_deferral_is_dropped_when_it_no_longer_applies(change):
    snapshot = load("low_reserve_snapshot")
    snapshot.charge_options = []
    snapshot.charge_decision_deferrals = {"shuttle-05": 2}
    change(snapshot)

    assert with_battery_decisions(ENERGY_R1, snapshot).charge_decision_deferrals == {}


"""
# -----------------------------------------------------------------------------------
# Function: test_vehicle_option_ids_match_the_options_issued
# -----------------------------------------------------------------------------------
Description:
    The exact option IDs for a shuttle cover its CONTINUE option and one
    CHARGE_NOW per charger, and never another shuttle whose ID shares a
    prefix.

"""
def test_vehicle_option_ids_match_the_options_issued():
    snapshot = load("low_reserve_snapshot")

    ids = vehicle_option_ids(snapshot, "shuttle-05")

    assert ids == {"charge-option-shuttle-05-continue", "charge-option-shuttle-05-charger-1",
                   "charge-option-shuttle-05-charger-2"}
    assert {o.option_id for o in snapshot.charge_options} <= ids
    assert not vehicle_option_ids(snapshot, "shuttle-0") & ids


"""
# -----------------------------------------------------------------------------------
# Function: test_only_serving_shuttles_are_offered_continue
# -----------------------------------------------------------------------------------
Description:
    A low shuttle that is not serving (STANDBY or AWAITING_GUIDANCE) can
    still charge, but is not offered CONTINUE, since it is not in service.

"""
@pytest.mark.parametrize("state", ["STANDBY", "AWAITING_GUIDANCE"])
def test_only_serving_shuttles_are_offered_continue(state):
    snapshot = load("low_reserve_snapshot")
    snapshot.vehicles[4].service_state = state

    assert {o.action for o in with_results(snapshot).charge_options} == {"CHARGE_NOW"}
