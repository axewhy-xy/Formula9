"""
Tests for applying charger reservations (backend/src/services/battery_decision.py).

Author: Choo Jian Wei
Date: 24/10/2026
Last Modified: 26/10/2026

Description:
    Part of BE-M2-05 (battery decision). Runs RESERVE_CHARGER commands
    through a real TrainingStore loaded with the low-reserve fixture: a
    valid choice takes a port, sends the shuttle to the depot, withdraws its
    options and records the reason, response time and consequence; every
    stale, mismatched or impossible choice is rejected with the right code,
    and a busy charger never accepts a second reservation.

"""

import hashlib
import json
import sqlite3
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from contracts.v2.models import DispatcherCommand, FullSnapshot
from src.api.routes.training_v2 import get_actor_id, get_training_store
from src.app import app
from src.energy.assessment import score_charge_decision
from src.energy.decisions import with_battery_decisions
from src.energy.model import ENERGY_MODELS, ENERGY_R1, EnergyModel
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.services.battery_decision import finish_charging, submit_battery_decision

FIXTURES = Path(__file__).resolve().parents[2] / "contracts" / "v2" / "fixtures"
OPTION = "charge-option-shuttle-05-charger-1"


def low_reserve(**changes) -> dict:
    value = json.loads((FIXTURES / "low_reserve_snapshot.json").read_text(encoding="utf-8"))
    value.update(changes)
    return value


def reservation(key: str = "key-1", target: str = "charger-1", option: str = OPTION,
                expected_revision: int = 1, option_revision: int = 1,
                command_id: str | None = None) -> DispatcherCommand:
    return DispatcherCommand(contract_version="2.0", command_id=command_id or f"command-{key}",
                             idempotency_key=key, session_id="session-001", command_type="RESERVE_CHARGER",
                             expected_epoch=1, expected_revision=expected_revision, target_type="CHARGER",
                             target_id=target, payload={"option_id": option, "option_revision": option_revision},
                             reason="Shuttle-05 is near reserve")


def reserve(store: TrainingStore, command: DispatcherCommand):
    """Submits the command the way the endpoint does; returns the final status."""
    return submit_battery_decision(store, "actor-a", command)


@pytest.fixture
def store(tmp_path: Path):
    with TrainingStore(tmp_path / "training.sqlite") as value:
        yield value


# ===================================================================================
# Valid reservation
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_valid_reservation_commits_port_state_and_options
# -----------------------------------------------------------------------------------
Description:
    The shuttle takes the charger's port and heads to the depot, both
    revisions rise, and its options are withdrawn. The committed snapshot
    still passes every contract rule (it is reloaded from the store).

"""
def test_valid_reservation_commits_port_state_and_options(store):
    store.create_session(FullSnapshot.model_validate(low_reserve()))

    status = reserve(store, reservation())

    assert status.status == "APPLIED"
    after = store.snapshot("session-001")
    charger = next(c for c in after.chargers if c.charger_id == "charger-1")
    shuttle = next(v for v in after.vehicles if v.vehicle_id == "shuttle-05")
    assert charger.occupied_vehicle_ids == ["shuttle-05"]
    assert charger.aggregate_revision == 2
    assert shuttle.service_state == "RETURNING_TO_DEPOT"
    assert shuttle.aggregate_revision == 2
    assert after.charge_options == []
    assert next(c for c in after.chargers if c.charger_id == "charger-2").occupied_vehicle_ids == []


"""
# -----------------------------------------------------------------------------------
# Function: test_reservation_event_records_reason_response_time_and_consequence
# -----------------------------------------------------------------------------------
Description:
    The CHARGER_RESERVED event is the durable record BE-M2-05 asks for: the
    chosen option, the reason, the response time since the option was
    offered (tick 200 -> 260 is 3 s), the charging consequence, and the
    provisional battery score of the decision as made (100 here).

"""
@pytest.mark.parametrize("coverage", [None, 0, 4])
def test_reservation_event_records_reason_response_time_and_consequence(store, coverage):
    snapshot = FullSnapshot.model_validate(low_reserve(tick=260, simulation_time_s=13.0))
    snapshot_vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == "shuttle-05")
    snapshot_option = next(o for o in snapshot.charge_options if o.option_id == OPTION)
    snapshot_option.min_in_service_vehicles = coverage
    store.create_session(snapshot)

    status = reserve(store, reservation())

    events = store.replay("session-001", 7).events
    event = next(e for e in events if e.event_type == "CHARGER_RESERVED")
    assert status.result_event_id is not None
    assert (event.entity_type, event.entity_id, event.entity_revision) == ("CHARGER", "charger-1", 2)
    assert event.data == {
        "booking_id": next(booking.booking_id for booking in store.snapshot(snapshot.session_id).charge_bookings
                           if booking.vehicle_id == "shuttle-05" and booking.status == "ACTIVE"),
        "option_id": OPTION, "option_revision": 1, "vehicle_id": "shuttle-05", "reason": "Shuttle-05 is near reserve",
        "response_time_s": pytest.approx(3.0), "out_of_service_s": 1200.0,
        "battery_after_kwh": 22.0, "loops_after": 42, "min_in_service_vehicles": coverage, "rejected_attempts": 0,
        "assessment": score_charge_decision(ENERGY_R1, snapshot_vehicle, snapshot_option, 3.0),
    }
    assert event.data["assessment"]["total"] == 100.0
    assert store.snapshot("session-001").charge_options == []


"""
# -----------------------------------------------------------------------------------
# Function: test_other_shuttles_keep_their_options
# -----------------------------------------------------------------------------------
Description:
    With two low shuttles, reserving for one withdraws only that shuttle's
    options. The other shuttle's offers are reissued: its coverage counts
    the reserved shuttle as out of service (5 -> 4 and 4 -> 3), and its
    charge offer moves to charger-2 now that charger-1 is full.

"""
def test_other_shuttles_keep_their_options(store):
    snapshot = FullSnapshot.model_validate(low_reserve())
    snapshot.vehicles[3].battery_kwh = 7.0
    store.create_session(with_battery_decisions(ENERGY_R1, snapshot))

    reserve(store, reservation())

    after = store.snapshot("session-001")
    assert {o.vehicle_id for o in after.charge_options} == {"shuttle-04"}
    assert {(o.action, o.charger_id, o.revision, o.min_in_service_vehicles) for o in after.charge_options} == {
        ("CONTINUE", None, 2, 4), ("CHARGE_NOW", "charger-2", 1, 3)}
    assert after.charge_option_revisions["charge-option-shuttle-04-charger-1"] == 1


# ===================================================================================
# Rejections
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_invalid_reservations_are_rejected_without_changes
# -----------------------------------------------------------------------------------
Description:
    Each precondition has its own rejection code, and a rejected command
    leaves the charger, the shuttle and the options untouched.

"""
def _busy(value: dict) -> None:
    value["chargers"][0]["occupied_vehicle_ids"] = ["shuttle-01"]
    value["vehicles"][0]["service_state"] = "CHARGING"


@pytest.mark.parametrize(("change", "command", "code"), [
    (None, reservation(option="charge-option-unknown"), "OPTION_EXPIRED"),
    (lambda v: v.update(tick=301, simulation_time_s=15.05), reservation(), "OPTION_EXPIRED"),
    (None, reservation(option="charge-option-shuttle-05-continue"), "INVALID_PAYLOAD"),
    (None, reservation(target="charger-2"), "INVALID_PAYLOAD"),
    (lambda v: v["vehicles"][4].update(aggregate_revision=2), reservation(), "STALE_REVISION"),
    (lambda v: v["vehicles"][4].update(service_state="BLOCKED"), reservation(), "INVALID_TRANSITION"),
    (_busy, reservation(), "CHARGER_CONFLICT"),
], ids=["unknown-option", "expired", "continue-option", "other-charger", "shuttle-changed",
        "cannot-return", "charger-busy"])
def test_invalid_reservations_are_rejected_without_changes(store, change, command, code):
    value = low_reserve()
    if change:
        change(value)
    store.create_session(FullSnapshot.model_validate(value))
    before = store.snapshot("session-001")

    status = reserve(store, command)

    assert (status.status, status.failure_code) == ("REJECTED", code)
    after = store.snapshot("session-001")
    assert after.chargers == before.chargers
    assert after.vehicles == before.vehicles
    assert after.charge_options == before.charge_options


"""
# -----------------------------------------------------------------------------------
# Function: test_return_to_depot_option_cannot_reserve_a_charger
# -----------------------------------------------------------------------------------
Description:
    Only CHARGE_NOW reserves a port. A RETURN_TO_DEPOT option may name the
    same charger, but using it for RESERVE_CHARGER is rejected.

"""
def test_return_to_depot_option_cannot_reserve_a_charger(store):
    value = low_reserve()
    return_option = dict(value["charge_options"][1], option_id="charge-option-shuttle-05-return",
                         action="RETURN_TO_DEPOT")
    value["charge_options"].append(return_option)
    value["charge_option_revisions"][return_option["option_id"]] = return_option["revision"]
    store.create_session(FullSnapshot.model_validate(value))

    status = reserve(store, reservation(option="charge-option-shuttle-05-return"))

    assert (status.status, status.failure_code) == ("REJECTED", "INVALID_PAYLOAD")


"""
# -----------------------------------------------------------------------------------
# Function: test_busy_charger_never_takes_a_second_reservation
# -----------------------------------------------------------------------------------
Description:
    BE-M2-05 verification: two low shuttles are both offered charger-1's
    single port. The first reservation wins. The second is durably rejected
    whether it still expects the old charger revision or the new one (its
    option depends on the old revision), and the port still holds only the
    first shuttle.

"""
def test_busy_charger_never_takes_a_second_reservation(store):
    snapshot = FullSnapshot.model_validate(low_reserve())
    snapshot.vehicles[3].battery_kwh = 7.0
    store.create_session(with_battery_decisions(ENERGY_R1, snapshot))
    second_option = "charge-option-shuttle-04-charger-1"

    first = reserve(store, reservation())
    old_revision = reserve(store, reservation(key="key-2", option=second_option))
    new_revision = reserve(store, reservation(key="key-3", option=second_option, expected_revision=2))

    assert first.status == "APPLIED"
    assert (old_revision.status, old_revision.failure_code) == ("REJECTED", "STALE_REVISION")
    # charger-1 is now full, so shuttle-04's offer there has been withdrawn
    assert (new_revision.status, new_revision.failure_code) == ("REJECTED", "OPTION_EXPIRED")
    charger = next(c for c in store.snapshot("session-001").chargers if c.charger_id == "charger-1")
    assert charger.occupied_vehicle_ids == ["shuttle-05"]


"""
# -----------------------------------------------------------------------------------
# Function: test_commands_endpoint_applies_reservation_immediately
# -----------------------------------------------------------------------------------
Description:
    Through the real HTTP endpoint, a RESERVE_CHARGER comes back APPLIED
    (not left QUEUED) and the snapshot shows the reserved port; a retry
    with the same idempotency key returns the same result without applying
    it twice.

"""
def test_commands_endpoint_applies_reservation_immediately(store):
    store.create_session(FullSnapshot.model_validate(low_reserve()))
    app.dependency_overrides[get_training_store] = lambda: store
    app.dependency_overrides[get_actor_id] = lambda: "actor-a"
    try:
        with TestClient(app) as client:
            body = reservation().model_dump(mode="json")
            first = client.post("/api/v2/sessions/session-001/commands", json=body)
            retry = client.post("/api/v2/sessions/session-001/commands", json=body)
    finally:
        app.dependency_overrides.clear()

    assert first.status_code == 202
    assert first.json()["status"] == "APPLIED"
    assert retry.json()["status"] == "APPLIED"
    assert retry.json()["result_event_id"] == first.json()["result_event_id"]
    charger = next(c for c in store.snapshot("session-001").chargers if c.charger_id == "charger-1")
    assert charger.occupied_vehicle_ids == ["shuttle-05"]
    assert charger.aggregate_revision == 2


"""
# -----------------------------------------------------------------------------------
# Function: test_same_option_cannot_be_used_twice
# -----------------------------------------------------------------------------------
Description:
    Once applied, the option is withdrawn, so sending it again (as a new
    command at the charger's new revision) is rejected as expired.

"""
def test_same_option_cannot_be_used_twice(store):
    store.create_session(FullSnapshot.model_validate(low_reserve()))
    reserve(store, reservation())

    again = reserve(store, reservation(key="key-2", expected_revision=2))

    assert (again.status, again.failure_code) == ("REJECTED", "OPTION_EXPIRED")


# ===================================================================================
# Review fixes (PR #52)
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_failed_or_unknown_options_cannot_reserve
# -----------------------------------------------------------------------------------
Description:
    The shared validator rejects a CHARGE_NOW offer whose validation is FAIL
    or UNKNOWN before any port is reserved.

"""
@pytest.mark.parametrize("validation", ["FAIL", "UNKNOWN"])
def test_failed_or_unknown_options_cannot_reserve(store, validation):
    value = low_reserve()
    value["charge_options"][1]["validation"] = validation
    store.create_session(FullSnapshot.model_validate(value))

    status = reserve(store, reservation())

    assert (status.status, status.failure_code) == ("REJECTED", "UNKNOWN_CONSTRAINT")
    charger = next(c for c in store.snapshot("session-001").chargers if c.charger_id == "charger-1")
    assert charger.occupied_vehicle_ids == []


"""
# -----------------------------------------------------------------------------------
# Function: test_choice_of_an_older_offer_version_is_rejected
# -----------------------------------------------------------------------------------
Description:
    When a dependency changes, the offer is reissued with the next revision
    and new numbers. A command that chose the old revision (the 22 kWh
    offer) is rejected instead of silently recording the new consequence.

"""
def test_choice_of_an_older_offer_version_is_rejected(store):
    snapshot = FullSnapshot.model_validate(low_reserve())
    snapshot.vehicles[4].aggregate_revision = 2
    snapshot.vehicles[4].battery_kwh = 6.8
    snapshot = with_battery_decisions(ENERGY_R1, snapshot)
    reissued = next(o for o in snapshot.charge_options if o.option_id == OPTION)
    assert reissued.revision == 2 and reissued.battery_after_kwh != 22.0
    store.create_session(snapshot)

    stale = reserve(store, reservation(option_revision=1))
    current = reserve(store, reservation(key="key-2", option_revision=2))

    assert (stale.status, stale.failure_code) == ("REJECTED", "STALE_REVISION")
    assert current.status == "APPLIED"
    event = next(e for e in store.replay("session-001", 7).events if e.event_type == "CHARGER_RESERVED")
    assert event.data["option_revision"] == 2
    assert event.data["battery_after_kwh"] == pytest.approx(reissued.battery_after_kwh)


"""
# -----------------------------------------------------------------------------------
# Function: test_retry_with_new_command_id_resumes_the_journaled_command
# -----------------------------------------------------------------------------------
Description:
    A retry with the same idempotency key but a new command ID resumes the
    original queued command (no 404, nothing left QUEUED), and a later retry
    returns that same applied outcome.

"""
def test_retry_with_new_command_id_resumes_the_journaled_command(store):
    store.create_session(FullSnapshot.model_validate(low_reserve()))
    original = reservation(command_id="command-original")
    store.accept("actor-a", original, defer_preconditions=True)  # journaled, then the reply was lost

    resumed = reserve(store, reservation(command_id="command-retry"))
    again = reserve(store, reservation(command_id="command-retry-2"))

    assert (resumed.status, resumed.command_id) == ("APPLIED", "command-original")
    assert (again.status, again.command_id, again.result_event_id) == (
        "APPLIED", "command-original", resumed.result_event_id)
    assert store.command_status("session-001", "actor-a", "key-1").status == "APPLIED"



"""
# -----------------------------------------------------------------------------------
# Function: test_old_choice_cannot_match_an_offer_recreated_after_withdrawal
# -----------------------------------------------------------------------------------
Description:
    Review scenario: shuttle-05 is offered revision 1, gets blocked (offers
    withdrawn), then returns to service. The re-created offer is revision 2,
    so a choice of the old revision 1 is rejected as stale.

"""
def test_old_choice_cannot_match_an_offer_recreated_after_withdrawal(store):
    snapshot = with_battery_decisions(ENERGY_R1, FullSnapshot.model_validate(low_reserve()))
    snapshot.vehicles[4].service_state = "BLOCKED"
    snapshot = with_battery_decisions(ENERGY_R1, snapshot)
    snapshot.vehicles[4].service_state = "IN_SERVICE"
    snapshot = with_battery_decisions(ENERGY_R1, snapshot)
    store.create_session(snapshot)

    old_choice = reserve(store, reservation(option_revision=1))
    new_choice = reserve(store, reservation(key="key-2", option_revision=2))

    assert (old_choice.status, old_choice.failure_code) == ("REJECTED", "STALE_REVISION")
    assert new_choice.status == "APPLIED"


# ===================================================================================
# Scoring inputs
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_slow_decision_is_timed_from_the_first_offer
# -----------------------------------------------------------------------------------
Description:
    Offers expire every 5 s and are reissued, but the response time still
    runs from the first offer (tick 200). Choosing the revision-2 offer at
    tick 1600 records 70 s and loses response-time points.

"""
def test_slow_decision_is_timed_from_the_first_offer(store):
    snapshot = FullSnapshot.model_validate(low_reserve())
    snapshot.tick, snapshot.simulation_time_s = 1600, 80.0
    snapshot = with_battery_decisions(ENERGY_R1, snapshot)
    store.create_session(snapshot)

    status = reserve(store, reservation(option_revision=2))

    event = next(e for e in store.replay("session-001", 7).events if e.event_type == "CHARGER_RESERVED")
    assert status.status == "APPLIED"
    assert event.data["response_time_s"] == pytest.approx(70.0)
    assert event.data["assessment"]["criteria"]["response_time"] < 30


"""
# -----------------------------------------------------------------------------------
# Function: test_score_uses_the_scenarios_energy_model
# -----------------------------------------------------------------------------------
Description:
    A scenario with a 25% reserve (10 kWh of 40) is scored with that model:
    a shuttle holding 7 kWh is already below reserve and gets no
    reserve-protection points, although ENERGY_R1 would give full points.

"""
def test_score_uses_the_scenarios_energy_model(store, monkeypatch):
    strict = EnergyModel(revision="energy-strict", drive_kwh_per_km=0.25, auxiliary_kw=1.5,
                         charge_efficiency=0.9, reserve_fraction=0.25)
    monkeypatch.setitem(ENERGY_MODELS, strict.revision, strict)
    snapshot = FullSnapshot.model_validate(low_reserve())
    snapshot.scenario.energy_model_revision = strict.revision
    snapshot.charge_options = []
    snapshot = with_battery_decisions(strict, snapshot)
    vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == "shuttle-05")
    option = next(o for o in snapshot.charge_options if o.option_id == OPTION)
    store.create_session(snapshot)

    reserve(store, reservation(option_revision=2))

    event = next(e for e in store.replay("session-001", 7).events if e.event_type == "CHARGER_RESERVED")
    assert event.data["assessment"] == score_charge_decision(strict, vehicle, option, 0.0)
    assert event.data["assessment"]["criteria"]["reserve_protection"] == 0


"""
# -----------------------------------------------------------------------------------
# Function: test_unknown_energy_model_still_reserves_without_a_score
# -----------------------------------------------------------------------------------
Description:
    If the scenario names an energy model the backend does not have, the
    reservation still applies, but the score is null rather than computed
    with the wrong model. The other low shuttle's offers cannot be
    recomputed, so they are withdrawn instead of keeping stale coverage;
    their revisions stay in the history.

"""
def test_unknown_energy_model_still_reserves_without_a_score(store):
    snapshot = FullSnapshot.model_validate(low_reserve())
    snapshot.vehicles[3].battery_kwh = 7.0
    snapshot = with_battery_decisions(ENERGY_R1, snapshot)
    snapshot.scenario.energy_model_revision = "energy-unknown"
    for forecast in snapshot.energy_forecasts:
        forecast.energy_model_revision = "energy-unknown"
    store.create_session(snapshot)

    status = reserve(store, reservation())

    event = next(e for e in store.replay("session-001", 7).events if e.event_type == "CHARGER_RESERVED")
    assert status.status == "APPLIED"
    assert event.data["assessment"] is None
    after = store.snapshot("session-001")
    assert after.charge_options == []
    assert after.charge_option_revisions["charge-option-shuttle-04-continue"] == 1


# ===================================================================================
# CONTINUE_SERVICE
# ===================================================================================

CONTINUE = "charge-option-shuttle-05-continue"


def keep_in_service(key: str = "continue-1", target: str = "shuttle-05", option: str = CONTINUE,
                    expected_revision: int = 1, option_revision: int = 1) -> DispatcherCommand:
    return DispatcherCommand(contract_version="2.0", command_id=f"command-{key}", idempotency_key=key,
                             session_id="session-001", command_type="CONTINUE_SERVICE", expected_epoch=1,
                             expected_revision=expected_revision, target_type="VEHICLE", target_id=target,
                             payload={"option_id": option, "option_revision": option_revision},
                             reason="Two loops left; the peak is nearly over")


def decision_event(store: TrainingStore, event_type: str):
    return next(e for e in store.replay("session-001", 7, 500).events if e.event_type == event_type)


"""
# -----------------------------------------------------------------------------------
# Function: test_continue_service_records_the_decision_and_defers_it
# -----------------------------------------------------------------------------------
Description:
    Choosing CONTINUE keeps the shuttle in service (its revision rises),
    withdraws its options, records its loops remaining (2) as a deferral,
    and writes a SERVICE_CONTINUED event with the same decision record as a
    reservation. The score: 35 reserve + 25 time + 2.5 energy (2 of 20
    loops) + 15 attempts.

"""
def test_continue_service_records_the_decision_and_defers_it(store):
    store.create_session(FullSnapshot.model_validate(low_reserve()))

    status = reserve(store, keep_in_service())

    after = store.snapshot("session-001")
    shuttle = next(v for v in after.vehicles if v.vehicle_id == "shuttle-05")
    event = decision_event(store, "SERVICE_CONTINUED")
    assert status.status == "APPLIED"
    assert (shuttle.service_state, shuttle.aggregate_revision) == ("IN_SERVICE", 2)
    assert after.charge_options == []
    assert after.charge_decision_deferrals == {"shuttle-05": 2}
    assert all(c.occupied_vehicle_ids == [] for c in after.chargers)
    assert (event.entity_type, event.entity_id, event.entity_revision) == ("VEHICLE", "shuttle-05", 2)
    assert event.data["option_id"] == CONTINUE
    assert (event.data["out_of_service_s"], event.data["loops_after"], event.data["rejected_attempts"]) == (0.0, 2, 0)
    assert event.data["min_in_service_vehicles"] == 5
    assert event.data["assessment"]["criteria"] == {"reserve_protection": 35, "response_time": 25.0,
                                                    "energy_outcome": 2.5, "attempts": 15}


"""
# -----------------------------------------------------------------------------------
# Function: test_invalid_continue_commands_are_rejected_without_changes
# -----------------------------------------------------------------------------------
Description:
    CONTINUE_SERVICE goes through the same shared validator: a charge
    option, another shuttle, an outdated offer or shuttle revision, and an
    unknown shuttle are each rejected, leaving the snapshot unchanged.

"""
@pytest.mark.parametrize(("command", "code"), [
    (keep_in_service(option=OPTION), "INVALID_PAYLOAD"),
    (keep_in_service(target="shuttle-04"), "INVALID_PAYLOAD"),
    (keep_in_service(option_revision=2), "STALE_REVISION"),
    (keep_in_service(expected_revision=2), "STALE_REVISION"),
    (keep_in_service(target="shuttle-99"), "INVALID_REFERENCE"),
    (keep_in_service(option="charge-option-unknown"), "OPTION_EXPIRED"),
], ids=["charge-option", "other-shuttle", "old-offer", "shuttle-changed", "unknown-shuttle", "unknown-option"])
def test_invalid_continue_commands_are_rejected_without_changes(store, command, code):
    store.create_session(FullSnapshot.model_validate(low_reserve()))
    before = store.snapshot("session-001")

    status = reserve(store, command)

    after = store.snapshot("session-001")
    assert (status.status, status.failure_code) == ("REJECTED", code)
    assert after.vehicles == before.vehicles
    assert after.charge_options == before.charge_options
    assert after.charge_decision_deferrals == {}


"""
# -----------------------------------------------------------------------------------
# Function: test_commands_endpoint_applies_continue_immediately
# -----------------------------------------------------------------------------------
Description:
    Through the real HTTP endpoint, CONTINUE_SERVICE comes back APPLIED,
    and a retry with the same idempotency key returns the same result.

"""
def test_commands_endpoint_applies_continue_immediately(store):
    store.create_session(FullSnapshot.model_validate(low_reserve()))
    app.dependency_overrides[get_training_store] = lambda: store
    app.dependency_overrides[get_actor_id] = lambda: "actor-a"
    try:
        with TestClient(app) as client:
            body = keep_in_service().model_dump(mode="json")
            first = client.post("/api/v2/sessions/session-001/commands", json=body)
            retry = client.post("/api/v2/sessions/session-001/commands", json=body)
    finally:
        app.dependency_overrides.clear()

    assert (first.status_code, first.json()["status"], first.json()["target_type"]) == (202, "APPLIED", "VEHICLE")
    assert retry.json()["result_event_id"] == first.json()["result_event_id"]
    assert store.snapshot("session-001").charge_decision_deferrals == {"shuttle-05": 2}


# ===================================================================================
# Rejected attempts
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_rejected_tries_for_the_decision_cost_attempt_points
# -----------------------------------------------------------------------------------
Description:
    Two rejected tries for shuttle-05's decision (an outdated offer version,
    then its CONTINUE option sent to a charger) are counted when the
    reservation succeeds, leaving 5 of the 15 attempt points. A rejected try
    for shuttle-04 does not count against shuttle-05.

"""
def test_rejected_tries_for_the_decision_cost_attempt_points(store):
    snapshot = FullSnapshot.model_validate(low_reserve())
    snapshot.vehicles[3].battery_kwh = 7.0
    store.create_session(with_battery_decisions(ENERGY_R1, snapshot))

    assert reserve(store, reservation(key="try-1", option_revision=2)).failure_code == "STALE_REVISION"
    assert reserve(store, reservation(key="try-2", option=CONTINUE)).failure_code == "INVALID_PAYLOAD"
    other = reservation(key="try-3", option="charge-option-shuttle-04-charger-1", option_revision=2)
    assert reserve(store, other).failure_code == "STALE_REVISION"
    assert reserve(store, reservation(key="try-4")).status == "APPLIED"

    event = decision_event(store, "CHARGER_RESERVED")
    assert event.data["rejected_attempts"] == 2
    assert event.data["assessment"]["criteria"]["attempts"] == 5


"""
# -----------------------------------------------------------------------------------
# Function: test_tries_before_the_decision_was_offered_do_not_count
# -----------------------------------------------------------------------------------
Description:
    A rejected try from an earlier decision (before the current offer's
    offered_at_tick) is not held against the new one.

"""
def test_tries_before_the_decision_was_offered_do_not_count(store):
    store.create_session(FullSnapshot.model_validate(low_reserve()))
    assert reserve(store, reservation(key="old-try", option_revision=2)).status == "REJECTED"
    later = store.snapshot("session-001")
    later.vehicles[4].service_state = "BLOCKED"
    later = with_battery_decisions(ENERGY_R1, later)
    later.tick, later.simulation_time_s = 400, 20.0
    later.vehicles[4].service_state = "IN_SERVICE"
    later = with_battery_decisions(ENERGY_R1, later)
    store.commit_snapshot(later)

    assert reserve(store, reservation(key="new-try", option_revision=2)).status == "APPLIED"
    assert decision_event(store, "CHARGER_RESERVED").data["rejected_attempts"] == 0


"""
# -----------------------------------------------------------------------------------
# Function: test_store_lists_only_rejected_commands_of_the_given_types
# -----------------------------------------------------------------------------------
Description:
    rejected_commands returns rejected commands of the requested types, in
    order, and leaves out applied commands and other command types.

"""
def test_store_lists_only_rejected_commands_of_the_given_types(store):
    store.create_session(FullSnapshot.model_validate(low_reserve()))
    reserve(store, reservation(key="stale", option_revision=2))
    reserve(store, keep_in_service(key="wrong", option=OPTION))
    reserve(store, reservation(key="good"))

    keys = lambda types: [c.idempotency_key for c in store.rejected_commands("session-001", types, 0)]

    assert keys({"RESERVE_CHARGER", "CONTINUE_SERVICE"}) == ["stale", "wrong"]
    assert keys({"RESERVE_CHARGER"}) == ["stale"]
    assert store.rejected_commands("session-001", {"RESERVE_CHARGER"}, 201) == []


# ===================================================================================
# Port release
# ===================================================================================

def charging(value: dict) -> dict:
    """shuttle-05 charging on charger-1 and shuttle-03 on charger-2; both ports taken."""
    value["chargers"][0]["occupied_vehicle_ids"] = ["shuttle-05"]
    value["chargers"][1]["occupied_vehicle_ids"] = ["shuttle-03"]
    value["vehicles"][4]["service_state"] = "CHARGING"
    value["vehicles"][2]["service_state"] = "CHARGING"
    value["charge_options"], value["charge_option_revisions"] = [], {}
    return value


"""
# -----------------------------------------------------------------------------------
# Function: test_finished_charging_frees_the_port_and_stands_the_shuttle_by
# -----------------------------------------------------------------------------------
Description:
    After 20 minutes on a 50 kW charger at 90% efficiency, shuttle-05 gains
    15 kWh (7 -> 22), moves to STANDBY and leaves charger-1; both revisions
    rise. The result commits through the store and the event records it.

"""
def test_finished_charging_frees_the_port_and_stands_the_shuttle_by(store):
    store.create_session(FullSnapshot.model_validate(charging(low_reserve())))

    proposed, event = finish_charging(store.snapshot("session-001"), "shuttle-05", 1200.0)
    after = store.commit_snapshot(proposed, (event,))

    charger = next(c for c in after.chargers if c.charger_id == "charger-1")
    shuttle = next(v for v in after.vehicles if v.vehicle_id == "shuttle-05")
    assert (charger.occupied_vehicle_ids, charger.aggregate_revision) == ([], 2)
    assert (shuttle.service_state, shuttle.aggregate_revision) == ("STANDBY", 2)
    assert shuttle.battery_kwh == pytest.approx(22.0)
    assert next(c for c in after.chargers if c.charger_id == "charger-2").occupied_vehicle_ids == ["shuttle-03"]
    assert (event.event_type, event.entity_id, event.entity_revision) == ("CHARGING_FINISHED", "charger-1", 2)
    assert event.data == {"vehicle_id": "shuttle-05", "charged_s": 1200.0,
                          "energy_added_kwh": pytest.approx(15.0), "battery_kwh": pytest.approx(22.0)}


"""
# -----------------------------------------------------------------------------------
# Function: test_freed_port_is_offered_to_a_waiting_low_shuttle
# -----------------------------------------------------------------------------------
Description:
    With both ports taken, low shuttle-04 can only CONTINUE. Once
    shuttle-05 finishes, the offers are rebuilt and shuttle-04 can charge
    at the freed charger-1.

"""
def test_freed_port_is_offered_to_a_waiting_low_shuttle():
    value = charging(low_reserve())
    value["vehicles"][3]["battery_kwh"] = 7.0
    snapshot = with_battery_decisions(ENERGY_R1, FullSnapshot.model_validate(value))
    assert {o.action for o in snapshot.charge_options} == {"CONTINUE"}

    after, _ = finish_charging(snapshot, "shuttle-05", 1200.0)

    assert {(o.vehicle_id, o.action, o.charger_id) for o in after.charge_options} == {
        ("shuttle-04", "CONTINUE", None), ("shuttle-04", "CHARGE_NOW", "charger-1")}


"""
# -----------------------------------------------------------------------------------
# Function: test_charging_is_capped_at_capacity
# -----------------------------------------------------------------------------------
Description:
    A very long session fills the battery to capacity and no further.

"""
def test_charging_is_capped_at_capacity():
    snapshot = FullSnapshot.model_validate(charging(low_reserve()))

    after, event = finish_charging(snapshot, "shuttle-05", 36000.0)

    assert next(v for v in after.vehicles if v.vehicle_id == "shuttle-05").battery_kwh == 40.0
    assert event.data["energy_added_kwh"] == pytest.approx(33.0)


"""
# -----------------------------------------------------------------------------------
# Function: test_finish_charging_refuses_a_shuttle_that_is_not_charging
# -----------------------------------------------------------------------------------
Description:
    Only a CHARGING shuttle on a port can finish; an unknown shuttle or
    energy model is an invalid reference.

"""
@pytest.mark.parametrize(("change", "vehicle_id", "code"), [
    (None, "shuttle-01", "INVALID_TRANSITION"),
    (lambda v: v["vehicles"][4].update(service_state="RETURNING_TO_DEPOT"), "shuttle-05", "INVALID_TRANSITION"),
    (None, "shuttle-99", "INVALID_REFERENCE"),
    (lambda v: v["scenario"].update(energy_model_revision="energy-unknown"), "shuttle-05", "INVALID_REFERENCE"),
], ids=["in-service", "reserved-not-charging", "unknown-shuttle", "unknown-model"])
def test_finish_charging_refuses_a_shuttle_that_is_not_charging(change, vehicle_id, code):
    value = charging(low_reserve())
    if change is not None:
        change(value)
        for forecast in value["energy_forecasts"]:
            forecast["energy_model_revision"] = value["scenario"]["energy_model_revision"]
    snapshot = FullSnapshot.model_validate(value)

    with pytest.raises(TrainingStoreError) as error:
        finish_charging(snapshot, vehicle_id, 1200.0)

    assert error.value.code == code


# ===================================================================================
# Review fixes (PR #62)
# ===================================================================================

"""
# -----------------------------------------------------------------------------------
# Function: test_malformed_rejected_option_id_does_not_break_later_decisions
# -----------------------------------------------------------------------------------
Description:
    A command whose option_id is not a string (a list or an object) is
    durably rejected, and later valid decisions still apply; the malformed
    command does not count as an attempt at this shuttle's option.

"""
@pytest.mark.parametrize("bad_option", [[], {"id": OPTION}], ids=["list", "object"])
def test_malformed_rejected_option_id_does_not_break_later_decisions(store, bad_option):
    store.create_session(FullSnapshot.model_validate(low_reserve()))
    bad = reservation(key="bad").model_copy(update={"payload": {"option_id": bad_option, "option_revision": 1}})

    assert reserve(store, bad).status == "REJECTED"
    assert reserve(store, keep_in_service(key="keep")).status == "APPLIED"
    assert decision_event(store, "SERVICE_CONTINUED").data["rejected_attempts"] == 0


"""
# -----------------------------------------------------------------------------------
# Function: test_continue_at_zero_loops_asks_again
# -----------------------------------------------------------------------------------
Description:
    Choosing CONTINUE with zero loops left records no deferral (there is no
    further loop to wait for), so the next snapshot offers the decision
    again.

"""
def test_continue_at_zero_loops_asks_again(store):
    snapshot = FullSnapshot.model_validate(low_reserve())
    snapshot.vehicles[4].battery_kwh = 6.2
    snapshot = with_battery_decisions(ENERGY_R1, snapshot)
    keep = next(o for o in snapshot.charge_options if o.action == "CONTINUE")
    assert keep.loops_after == 0
    store.create_session(snapshot)

    status = reserve(store, keep_in_service(option_revision=keep.revision))

    after = store.snapshot("session-001")
    assert status.status == "APPLIED"
    assert after.charge_decision_deferrals == {}
    assert {o.vehicle_id for o in with_battery_decisions(ENERGY_R1, after).charge_options} == {"shuttle-05"}


"""
# -----------------------------------------------------------------------------------
# Function: test_continue_is_rejected_for_a_shuttle_that_is_not_serving
# -----------------------------------------------------------------------------------
Description:
    A CONTINUE offer for a shuttle that is no longer serving (for example
    one made before it went to STANDBY) is rejected with
    INVALID_TRANSITION, leaving its state, options and deferrals unchanged.

"""
@pytest.mark.parametrize("state", ["STANDBY", "AWAITING_GUIDANCE"])
def test_continue_is_rejected_for_a_shuttle_that_is_not_serving(store, state):
    value = low_reserve()
    value["vehicles"][4]["service_state"] = state
    store.create_session(FullSnapshot.model_validate(value))
    before = store.snapshot("session-001")

    status = reserve(store, keep_in_service())

    after = store.snapshot("session-001")
    assert (status.status, status.failure_code) == ("REJECTED", "INVALID_TRANSITION")
    assert after.vehicles == before.vehicles
    assert after.charge_options == before.charge_options
    assert after.charge_decision_deferrals == {}


"""
# -----------------------------------------------------------------------------------
# Function: test_session_saved_with_a_zero_deferral_still_recovers
# -----------------------------------------------------------------------------------
Description:
    An earlier build stored zero deferrals. A database holding one (in the
    session row and its latest checkpoint, with matching hashes) still
    opens: recovery loads the session, and the zero entry is dropped as
    "not deferred".

"""
def test_session_saved_with_a_zero_deferral_still_recovers(tmp_path):
    path = tmp_path / "training.sqlite"
    with TrainingStore(path) as first:
        first.create_session(FullSnapshot.model_validate(low_reserve()))

    with sqlite3.connect(path) as connection:
        raw = connection.execute("SELECT snapshot_json FROM sessions WHERE session_id='session-001'").fetchone()[0]
        value = json.loads(raw)
        value["charge_decision_deferrals"] = {"shuttle-05": 0}
        saved = json.dumps(value)
        digest = hashlib.sha256(saved.encode("utf-8")).hexdigest()
        connection.execute("UPDATE sessions SET snapshot_json=?, state_hash=? WHERE session_id='session-001'",
                           (saved, digest))
        connection.execute("UPDATE checkpoints SET snapshot_json=?, state_hash=? WHERE session_id='session-001'",
                           (saved, digest))

    with TrainingStore(path) as reopened:
        recovered = reopened.snapshot("session-001")

    assert recovered.session.status == "PAUSED"
    assert recovered.charge_decision_deferrals == {}
