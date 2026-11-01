"""
Tests for the second depot and the automatic safety return (backend/src/services/integrated_actor.py).

Author: Choo Jian Wei
Date: 01/11/2026
Last Modified: 01/11/2026

Description:
    Checks that the live session has both Toa Payoh depots, that a shuttle is
    pointed at the nearest depot with a free charger (Saint Michael's once Toa
    Payoh is full), that CHARGE_NOW offers a charger at that depot, and that a
    shuttle nobody sends to charge turns back on its own at the last safe point,
    arrives at or above the 15% reserve and charges there.

"""

import pytest

from src.energy.decisions import charge_options, energy_forecasts
from src.energy.forecast import reserve_kwh
from src.energy.model import ENERGY_R1
from src.routing.build_singapore_fleet_fixture import low_battery_kwh
from src.services.integrated_actor import IntegratedSessionActor

TOA_PAYOH, SAINT_MICHAELS = "depot-toa-payoh-interchange", "depot-saint-michael-s-bus-terminal"


def running_actor():
    actor = IntegratedSessionActor(0, session_id="depot-test")
    actor.is_paused = False
    actor.modes = {s.ID: "DWELLING" if s.ID == 0 else "IN_SERVICE" for s in actor.shuttles}
    return actor


def fill(actor, depot_id):
    depot = next(d for d in actor.definition.depots if d["depot_id"] == depot_id)
    for charger in actor.domain.chargers:
        if charger.charger_id in depot["bays"]:
            charger.occupied_vehicle_ids.append("someone-else")


def run_until(actor, index, event_type, max_ticks=60000):
    """Ticks until shuttle `index` logs event_type; returns that event and every event it logged on the way."""
    vehicle_id = actor.domain.vehicles[index].vehicle_id
    seen = []
    for _ in range(max_ticks):
        actor.tick()
        mine = [e for e in actor.pending_events if e.entity_id == vehicle_id]
        actor.pending_events.clear()
        seen.extend(mine)
        found = next((e for e in mine if e.event_type == event_type), None)
        if found is not None:
            return found, seen
    pytest.fail(f"{vehicle_id} never logged {event_type}")


def test_the_session_has_both_depots_each_with_two_chargers():
    actor = IntegratedSessionActor(0, session_id="depot-test")
    assert [(d.depot_id, d.charger_ids) for d in actor.domain.depots] == [
        (TOA_PAYOH, ["charger-1", "charger-2"]), (SAINT_MICHAELS, ["charger-3", "charger-4"])]
    assert [c.charger_id for c in actor.domain.chargers] == ["charger-1", "charger-2", "charger-3", "charger-4"]


def test_a_shuttle_heads_for_toa_payoh_while_it_has_a_free_charger():
    actor = running_actor()
    for shuttle in actor.shuttles:
        depot, distance = actor.depot_choice(shuttle)
        assert depot["depot_id"] == TOA_PAYOH and distance > 0


def test_a_full_toa_payoh_sends_shuttles_to_saint_michaels_further_away():
    actor = running_actor()
    near = actor.depot_choice(actor.shuttles[1])[1]
    fill(actor, TOA_PAYOH)
    depot, far = actor.depot_choice(actor.shuttles[1])
    assert depot["depot_id"] == SAINT_MICHAELS and far > near


def test_with_every_charger_taken_the_nearest_depot_is_still_named():
    actor = running_actor()
    fill(actor, TOA_PAYOH)
    fill(actor, SAINT_MICHAELS)
    assert actor.depot_choice(actor.shuttles[1])[0]["depot_id"] == TOA_PAYOH


def test_forecasts_and_charge_now_follow_the_chosen_depot():
    actor = running_actor()
    fill(actor, TOA_PAYOH)
    snapshot = actor.generate_snapshot()
    filled = actor.battery_decisions(ENERGY_R1, snapshot)
    low = next(f for f in filled.energy_forecasts if f.vehicle_id == "shuttle-05")
    assert low.next_depot_id == SAINT_MICHAELS
    offers = [o for o in filled.charge_options if o.vehicle_id == "shuttle-05" and o.action == "CHARGE_NOW"]
    assert offers and all(o.charger_id in {"charger-3", "charger-4"} for o in offers)


def test_without_choices_the_decisions_keep_their_single_depot_behaviour():
    actor = running_actor()
    snapshot = actor.generate_snapshot()
    distances = actor.depot_distances()
    forecasts = energy_forecasts(ENERGY_R1, snapshot, depot_distances_m=distances, depot_speed_mps=10.0)
    assert {f.next_depot_id for f in forecasts} == {TOA_PAYOH}
    assert {o.charger_id for o in charge_options(ENERGY_R1, snapshot, 60.0) if o.charger_id} == {"charger-1"}


@pytest.mark.parametrize("full, depot_id, chargers", [
    (None, TOA_PAYOH, {"charger-1", "charger-2"}),
    (TOA_PAYOH, SAINT_MICHAELS, {"charger-3", "charger-4"}),
])
def test_an_ignored_low_shuttle_turns_back_in_time_and_charges_above_its_reserve(full, depot_id, chargers):
    actor = running_actor()
    if full:
        fill(actor, full)
    shuttle = actor.shuttles[1]
    shuttle.battery = (low_battery_kwh(actor, 1)-0.3)*100/actor.BATTERY_CAPACITY_KWH
    reserve = reserve_kwh(ENERGY_R1, actor.BATTERY_CAPACITY_KWH)
    turned, before = run_until(actor, 1, "SAFETY_RETURN")
    assert "BATTERY_EXHAUSTED" not in {e.event_type for e in before}
    assert turned.data["depot_id"] == depot_id and turned.data["charger_id"] in chargers
    assert turned.data["expected_arrival_kwh"] >= reserve
    assert turned.data["assessment"]["outcome"] == "MISSED_DECISION" and turned.data["assessment"]["total"] == 0
    assert actor.modes[1] == "RETURNING_TO_DEPOT"
    assert actor.domain.vehicles[1].vehicle_id in next(c for c in actor.domain.chargers
                                                       if c.charger_id == turned.data["charger_id"]).occupied_vehicle_ids
    run_until(actor, 1, "DEPOT_BAY_ARRIVED")
    assert shuttle.battery*actor.BATTERY_CAPACITY_KWH/100 >= reserve
    assert (shuttle.position.x, shuttle.position.y) == tuple(actor.definition.bay_positions[turned.data["charger_id"]])
    started = shuttle.battery
    run_until(actor, 1, "CHARGING_STARTED")
    for _ in range(200):
        actor.tick()
    assert shuttle.battery > started, "the charge rises at the depot"


def test_a_healthy_shuttle_does_not_turn_back():
    actor = running_actor()
    for _ in range(4000):
        actor.tick()
        assert not any(e.event_type.startswith("SAFETY_RETURN") and e.entity_id != "shuttle-05" for e in actor.pending_events)
        actor.pending_events.clear()


def test_with_no_free_charger_anywhere_it_carries_on_and_says_so_once():
    actor = running_actor()
    fill(actor, TOA_PAYOH)
    fill(actor, SAINT_MICHAELS)
    actor.shuttles[1].battery = (low_battery_kwh(actor, 1)-0.3)*100/actor.BATTERY_CAPACITY_KWH
    _, seen = run_until(actor, 1, "SAFETY_RETURN_UNAVAILABLE")
    for _ in range(400):
        actor.tick()
        seen.extend(e for e in actor.pending_events if e.entity_id == "shuttle-02")
        actor.pending_events.clear()
    assert [e.event_type for e in seen].count("SAFETY_RETURN_UNAVAILABLE") == 1
    assert actor.modes[1] != "RETURNING_TO_DEPOT"


def test_a_checkpoint_keeps_the_unavailable_note():
    actor = running_actor()
    actor.safety_unavailable = {1}
    assert actor.workflow_state()["safety_unavailable"] == [1]
