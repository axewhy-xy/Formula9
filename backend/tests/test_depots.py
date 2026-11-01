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
    arrives at or above the 15% reserve and charges there. Also covers a depot
    filling up late, a charger out of reach, a stranded shuttle freeing its port,
    letting riders off before the depot, loop ends, CONTINUE, and a restored checkpoint; the committed runs through
    the store (a detour, CONTINUE and recovery) are in test_training_integration.

"""

from types import SimpleNamespace

import pytest

from contracts.v2.models import FullSnapshot
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
    set_down, _ = run_until(actor, 1, "PASSENGERS_SET_DOWN")
    assert set_down.data["stop_id"] == turned.data["set_down_stop_id"], "riders get off before it heads in"
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


def low(actor, index, below=0.3):
    actor.shuttles[index].battery = (low_battery_kwh(actor, index)-below)*100/actor.BATTERY_CAPACITY_KWH


def quiet_actor():
    """A running actor where only the shuttle under test is low."""
    actor = running_actor()
    actor.shuttles[4].battery = 100.0
    actor.incident_due_tick = 10**12
    return actor


def above_every_depot(actor, index, spare=0.2):
    """A battery that reaches every depot above the reserve with spare kWh to go."""
    shuttle = actor.shuttles[index]
    reserve = reserve_kwh(ENERGY_R1, actor.BATTERY_CAPACITY_KWH)
    need = -min(actor._arrival_kwh(0.0, actor._depot_distance(shuttle, d)) for d in actor.definition.depots)
    shuttle.battery = (reserve+need+spare)*100/actor.BATTERY_CAPACITY_KWH


def test_toa_payoh_filling_up_late_still_leaves_saint_michaels_in_reach():
    first = quiet_actor()
    above_every_depot(first, 1)
    turned, _ = run_until(first, 1, "SAFETY_RETURN")
    assert turned.data["depot_id"] == TOA_PAYOH
    # The same run, but Toa Payoh fills up on the tick before the shuttle turned back
    second = quiet_actor()
    above_every_depot(second, 1)
    while second.current_tick < turned.tick-1:
        second.tick()
        second.pending_events.clear()
    fill(second, TOA_PAYOH)
    late, _ = run_until(second, 1, "SAFETY_RETURN")
    reserve = reserve_kwh(ENERGY_R1, second.BATTERY_CAPACITY_KWH)
    assert late.data["depot_id"] == SAINT_MICHAELS and late.data["expected_arrival_kwh"] >= reserve
    run_until(second, 1, "DEPOT_BAY_ARRIVED")
    assert second.shuttles[1].battery*second.BATTERY_CAPACITY_KWH/100 >= reserve


def test_a_charger_out_of_reach_is_not_reserved():
    actor = quiet_actor()
    actor.shuttles[1].battery = 0.01
    actor.tick()
    events = [e for e in actor.pending_events if e.entity_id == "shuttle-02"]
    assert [e.data["reason"] for e in events if e.event_type == "SAFETY_RETURN_UNAVAILABLE"] == ["CHARGER_OUT_OF_REACH"]
    assert "SAFETY_RETURN" not in {e.event_type for e in events}
    assert all("shuttle-02" not in c.occupied_vehicle_ids for c in actor.domain.chargers)


def test_a_shuttle_stranded_on_its_way_in_frees_its_port():
    actor = quiet_actor()
    shuttle = actor.shuttles[1]
    actor.domain.chargers[0].occupied_vehicle_ids.append("shuttle-02")
    points, _, _ = actor._depot_trip(shuttle, "charger-1", 10.0)
    actor._head_for_bay(shuttle, points)
    actor.modes[1] = "RETURNING_TO_DEPOT"
    shuttle.battery = 0.001
    exhausted, _ = run_until(actor, 1, "BATTERY_EXHAUSTED", max_ticks=200)
    assert exhausted.data["released_charger_id"] == "charger-1"
    assert actor.domain.chargers[0].occupied_vehicle_ids == [] and actor.modes[1] == "OUT_OF_SERVICE"


def test_turning_back_at_a_loop_end_records_the_loop_first():
    actor = quiet_actor()
    shuttle = actor.shuttles[1]
    route = actor._routes[shuttle.route_ID]
    shuttle.progress = route["total_length"]
    shuttle.position = actor._route_evaluation(shuttle.route_ID, shuttle.progress)
    actor.modes[1], shuttle.dwell_timer = "DWELLING", actor.DWELL_S
    reserve = reserve_kwh(ENERGY_R1, actor.BATTERY_CAPACITY_KWH)
    # Enough to get in from here, not enough for another loop first
    need = -min(actor._arrival_kwh(0.0, actor._depot_distance(shuttle, d)) for d in actor._guarded_depots(shuttle))
    shuttle.battery = (reserve+need+0.05)*100/actor.BATTERY_CAPACITY_KWH
    actor.loop_started[1] = (0, shuttle.battery*actor.BATTERY_CAPACITY_KWH/100)
    actor.tick()
    names = [e.event_type for e in actor.pending_events if e.entity_id == "shuttle-02"]
    assert names.index("LOOP_COMPLETED") < names.index("SAFETY_RETURN")
    assert 1 not in actor.loop_started, "the trip in is not counted as the next loop"


def test_continue_promises_only_the_loops_before_the_shuttle_turns_back():
    actor = running_actor()
    filled = actor.battery_decisions(ENERGY_R1, actor.generate_snapshot())
    forecast = next(f for f in filled.energy_forecasts if f.vehicle_id == "shuttle-05")
    option = next(o for o in filled.charge_options if o.vehicle_id == "shuttle-05" and o.action == "CONTINUE")
    assert option.loops_after <= forecast.loops_remaining and "on its own" in option.assumptions[0]
    turned, seen = run_until(actor, 4, "SAFETY_RETURN")
    loops = sum(e.event_type == "LOOP_COMPLETED" for e in seen)
    assert loops >= option.loops_after, "it was allowed every loop CONTINUE promised"


def test_a_safety_return_after_continue_is_not_scored_as_no_decision():
    actor = quiet_actor()
    low(actor, 1)
    actor.note_decisions([SimpleNamespace(event_type="SERVICE_CONTINUED", entity_id="shuttle-02", data={})])
    turned, _ = run_until(actor, 1, "SAFETY_RETURN")
    assert turned.data["after_decision"] == "CONTINUE" and turned.data["assessment"] is None
    assert "shuttle-02" not in actor.continued, "the next return is judged afresh"


def test_a_restored_checkpoint_keeps_the_safety_return_state():
    # restore_workflow brings back the workflow state; the shuttles' own state, and the whole
    # recovery through the session service, are covered in test_training_integration
    actor = quiet_actor()
    actor.note_decisions([SimpleNamespace(event_type="SERVICE_CONTINUED", entity_id="shuttle-02", data={})])
    actor.safety_unavailable = {3}
    state = actor.workflow_state()
    restored = IntegratedSessionActor(0, session_id="depot-test")
    restored.restore_workflow(state, FullSnapshot.model_validate(state["domain"]))
    assert restored.continued == {"shuttle-02"} and restored.safety_unavailable == {3}


def test_riders_get_off_at_the_next_stop_before_it_drives_in_empty():
    actor = quiet_actor()
    shuttle = actor.shuttles[1]
    reserve = reserve_kwh(ENERGY_R1, actor.BATTERY_CAPACITY_KWH)
    actor.tick()
    actor.pending_events.clear()
    assert actor.modes[1] == "IN_SERVICE", "driving between stops"
    # Inside the two-tick buffer (with the set-down dwell allowed for), so it turns back on the next tick
    need = -min(actor._arrival_kwh(0.0, actor._depot_distance(shuttle, d), actor.DWELL_S) for d in actor._guarded_depots(shuttle))
    shuttle.battery = (reserve+need+0.0001)*100/actor.BATTERY_CAPACITY_KWH
    route_id = actor.domain.vehicles[1].route_id
    names = actor.stop_names[shuttle.route_ID]
    ahead = sorted(p for p in actor.stop_progress[shuttle.route_ID] if p > shuttle.progress)
    actor.tick()
    turned = next(e for e in actor.pending_events if e.event_type == "SAFETY_RETURN")
    assert turned.data["set_down_stop_id"] == names[str(ahead[0])], "the next stop ahead"
    assert turned.data["stops_missed"] == [names[str(p)] for p in ahead[1:]] + [names["end"]]
    stop = next(s for s in next(r for r in actor.domain.routes if r.route_id == route_id).stops_m
                if s.stop_id == turned.data["set_down_stop_id"])
    held_ticks = 0
    for _ in range(4000):
        actor.tick()
        names_now = {e.event_type for e in actor.pending_events if e.entity_id == "shuttle-02"}
        actor.pending_events.clear()
        if abs(shuttle.position.x-stop.position.x_m) < 0.01 and abs(shuttle.position.y-stop.position.y_m) < 0.01:
            held_ticks += 1
        if "PASSENGERS_SET_DOWN" in names_now:
            break
    assert held_ticks >= round(actor.DWELL_S/actor.STEP_S)-1, "it stands at the stop for a dwell"
    assert "STOP_SERVED" not in names_now, "letting riders off is not serving the stop"
    arrived, seen = run_until(actor, 1, "DEPOT_BAY_ARRIVED")
    assert "STOP_SERVED" not in {e.event_type for e in seen}, "it passes the rest of its stops empty"
    assert shuttle.battery*actor.BATTERY_CAPACITY_KWH/100 >= reserve
