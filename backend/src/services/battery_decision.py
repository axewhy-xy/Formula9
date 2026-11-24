"""
Apply a trainee's battery decision to a v2 training session.

Author: Choo Jian Wei
Date Created: 24/10/2026
Last Modified: 26/10/2026

Description:
    Part of BE-M2-05 (battery decision). Resumes an accepted battery
    command at a simulation boundary, the same way route_edit.py applies
    route approvals: every precondition is checked again against the
    current snapshot, then the store either records a rejection or commits
    the new snapshot with a decision event.

    RESERVE_CHARGER targets a charger and names the CHARGE_NOW option the
    trainee chose; CONTINUE_SERVICE targets the shuttle and names its
    CONTINUE option. Both put the option ("option_id") and the offer version
    they saw ("option_revision") in the payload. The option checks live in
    the shared contract validator, so the store applies the same rules. A
    busy charger never accepts a second simultaneous reservation
    (CHARGER_CONFLICT).
"""
from collections.abc import Callable
from uuid import uuid4

from contracts.v2.models import (
    ChargeOption, CommandStatus, DispatcherCommand, Event, FleetVehicle, FullSnapshot, transition_allowed,
    validate_command_against_snapshot,
)
from src.energy.assessment import score_charge_decision
from src.energy.bookings import finish_hold, record_hold
from src.energy.chargers import ChargerConflict, ChargerState, release_port, reserve_port
from src.energy.decisions import IN_SERVICE_STATES, OPTION_LIFETIME_TICKS, vehicle_option_ids, with_battery_decisions
from src.energy.model import ENERGY_MODELS, EnergyModel, charge_battery_kwh
from src.persistence.training_store import TrainingStore, TrainingStoreError

TICK_S = 0.05  # one logical step is 50 ms
BATTERY_COMMANDS = {"RESERVE_CHARGER", "CONTINUE_SERVICE"}

"""
# -----------------------------------------------------------------------------------
# Function: submit_battery_decision
# -----------------------------------------------------------------------------------
Description:
    This function is the endpoint's entry point for RESERVE_CHARGER and
    CONTINUE_SERVICE. It journals the command first, deferring
    preconditions so even a stale or invalid choice gets one durable
    REJECTED outcome, then applies the persisted command. A retry with the
    same idempotency key (even with a new command ID) resumes or returns the
    original command instead of applying an unjournaled one.

Args:
    store (TrainingStore): The session journal.
    actor_id (str): The authenticated actor.
    command (DispatcherCommand): The submitted battery command.
    rebuild_decisions: Scenario-specific offer builder, preserving its travel,
        charging and parking assumptions when another shuttle reserves a port.

Returns:
    CommandStatus: The original outcome on a finished retry, otherwise
        APPLIED or REJECTED.
"""
def submit_battery_decision(store: TrainingStore, actor_id: str, command: DispatcherCommand, *,
                            rebuild_decisions: Callable[[EnergyModel, FullSnapshot], FullSnapshot] = with_battery_decisions) -> CommandStatus:
    receipt = store.accept(actor_id, command, defer_preconditions=True)
    if receipt.status != "QUEUED":
        return receipt
    _, accepted = store.command_record(command.session_id, actor_id, command.idempotency_key)
    if accepted.command_type == "CONTINUE_SERVICE":
        return apply_continue_service(store, accepted)
    return apply_charger_reservation(store, accepted, rebuild_decisions=rebuild_decisions)

"""
# -----------------------------------------------------------------------------------
# Function: apply_charger_reservation
# -----------------------------------------------------------------------------------
Description:
    This function applies one accepted RESERVE_CHARGER command. It rejects
    the command if the shared validator fails (option gone or expired, not a
    CHARGE_NOW for the targeted charger, a different offer version, FAIL or
    UNKNOWN validation, or a changed shuttle or charger), the shuttle cannot
    return to the depot, or the charger has no free port. Otherwise the
    shuttle takes a port and returns to the depot, its remaining options are
    withdrawn, and every other offer is rebuilt so its coverage numbers
    count the shuttle as out of service. A CHARGER_RESERVED event records
    the decision (see _decision_record). If the scenario's energy model is
    unknown, the score is null and every remaining offer is withdrawn,
    since its coverage can no longer be recomputed.

Args:
    store (TrainingStore): The session journal.
    command (DispatcherCommand): The accepted RESERVE_CHARGER command; its
        payload holds the chosen "option_id".

Returns:
    CommandStatus: APPLIED with the result event, or REJECTED with a code.
"""
def apply_charger_reservation(store: TrainingStore, command: DispatcherCommand, *,
                              rebuild_decisions: Callable[[EnergyModel, FullSnapshot], FullSnapshot] = with_battery_decisions) -> CommandStatus:
    snapshot = store.snapshot(command.session_id)
    try:
        failure = validate_command_against_snapshot(command, snapshot)
        if failure is not None:
            raise TrainingStoreError(failure.code, failure.message)
        option = next(o for o in snapshot.charge_options if o.option_id == command.payload["option_id"])
        vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == option.vehicle_id)
        if not transition_allowed("vehicle", vehicle.service_state, "RETURNING_TO_DEPOT"):
            raise TrainingStoreError("INVALID_TRANSITION", "Shuttle cannot return to the depot from its current state")
        states = [ChargerState(c.charger_id, c.port_count, c.occupied_vehicle_ids) for c in snapshot.chargers]
        try:
            reserved = reserve_port(states, command.target_id, vehicle.vehicle_id)
        except ChargerConflict:
            raise TrainingStoreError("CHARGER_CONFLICT", "Charger has no free port")
    except TrainingStoreError as error:
        return store.apply(command.command_id, rejection_code=error.code)

    proposed = snapshot.model_copy(deep=True)
    occupied = next(c for c in reserved if c.charger_id == command.target_id).occupied_vehicle_ids
    charger = next(c for c in proposed.chargers if c.charger_id == command.target_id)
    charger.occupied_vehicle_ids = list(occupied)
    charger.aggregate_revision += 1
    shuttle = next(v for v in proposed.vehicles if v.vehicle_id == vehicle.vehicle_id)
    shuttle.service_state = "RETURNING_TO_DEPOT"
    shuttle.aggregate_revision += 1
    booking = record_hold(proposed, vehicle.vehicle_id, charger.charger_id, proposed.tick, option.out_of_service_s, "CHARGE_NOW")
    model = ENERGY_MODELS.get(proposed.scenario.energy_model_revision)
    if model is not None:
        # Reissue the remaining offers: the reserved shuttle leaves service,
        # so other shuttles' coverage numbers change.
        proposed = rebuild_decisions(model, proposed)
    else:
        # Without the model the offers cannot be recomputed, and their coverage
        # numbers are now wrong, so withdraw them all. The revision history keeps
        # their last revisions, so an old choice cannot match a later offer.
        proposed.charge_options = []
    proposed.event_sequence += 1
    event = Event(event_id=str(uuid4()), event_sequence=proposed.event_sequence, tick=proposed.tick,
                  entity_type="CHARGER", entity_id=charger.charger_id,
                  entity_revision=charger.aggregate_revision, event_type="CHARGER_RESERVED",
                  source=proposed.source,
                  data={**_decision_record(store, snapshot, model, command, vehicle, option),
                        "booking_id": booking.booking_id})
    return store.apply(command.command_id, proposed, (event,))

"""
# -----------------------------------------------------------------------------------
# Function: apply_continue_service
# -----------------------------------------------------------------------------------
Description:
    This function applies one accepted CONTINUE_SERVICE command. It rejects
    the command if the shared validator fails (option gone or expired, not
    the CONTINUE option of the targeted shuttle, a different offer version,
    FAIL or UNKNOWN validation, or a changed shuttle), or with
    INVALID_TRANSITION if the shuttle is not serving (IN_SERVICE, DWELLING
    or DETOURING). Otherwise the shuttle stays in service, its revision
    rises by one, and its options are withdrawn.
    snapshot.charge_decision_deferrals records its loops remaining, so it is
    asked again only after using another loop; at zero loops nothing is
    recorded and it is asked again at once. Other
    shuttles' offers are untouched, since no shuttle leaves service. A
    SERVICE_CONTINUED event records the decision (see _decision_record).

Args:
    store (TrainingStore): The session journal.
    command (DispatcherCommand): The accepted CONTINUE_SERVICE command; its
        payload holds the chosen "option_id".

Returns:
    CommandStatus: APPLIED with the result event, or REJECTED with a code.
"""
def apply_continue_service(store: TrainingStore, command: DispatcherCommand) -> CommandStatus:
    snapshot = store.snapshot(command.session_id)
    failure = validate_command_against_snapshot(command, snapshot)
    if failure is not None:
        return store.apply(command.command_id, rejection_code=failure.code)
    option = next(o for o in snapshot.charge_options if o.option_id == command.payload["option_id"])
    vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == option.vehicle_id)
    if vehicle.service_state not in IN_SERVICE_STATES:
        return store.apply(command.command_id, rejection_code="INVALID_TRANSITION")

    proposed = snapshot.model_copy(deep=True)
    shuttle = next(v for v in proposed.vehicles if v.vehicle_id == vehicle.vehicle_id)
    shuttle.aggregate_revision += 1
    # At zero loops there is no further loop to wait for, so the shuttle is asked again at once.
    if option.loops_after:
        proposed.charge_decision_deferrals[vehicle.vehicle_id] = option.loops_after
    proposed.charge_options = [o for o in proposed.charge_options if o.vehicle_id != vehicle.vehicle_id]
    model = ENERGY_MODELS.get(proposed.scenario.energy_model_revision)
    proposed.event_sequence += 1
    event = Event(event_id=str(uuid4()), event_sequence=proposed.event_sequence, tick=proposed.tick,
                  entity_type="VEHICLE", entity_id=shuttle.vehicle_id,
                  entity_revision=shuttle.aggregate_revision, event_type="SERVICE_CONTINUED",
                  source=proposed.source,
                  data=_decision_record(store, snapshot, model, command, vehicle, option))
    return store.apply(command.command_id, proposed, (event,))

"""
# -----------------------------------------------------------------------------------
# Function: _decision_record
# -----------------------------------------------------------------------------------
Description:
    This function builds the data of a battery decision event: the chosen
    option, the reason, the response time (from the first offer of the
    decision), the option's consequence and coverage, how many of the
    trainee's battery commands for this shuttle were rejected since the
    decision was offered, and a provisional score (energy/assessment.py)
    using the scenario's energy model, or None when that model is unknown.

Args:
    store (TrainingStore): The session journal (for rejected attempts).
    snapshot (FullSnapshot): The snapshot the decision was made against.
    model (EnergyModel | None): The scenario's energy model, if known.
    command (DispatcherCommand): The accepted battery command.
    vehicle (FleetVehicle): The shuttle at the moment of the decision.
    option (ChargeOption): The chosen option.

Returns:
    dict: The event data.
"""
def _decision_record(store: TrainingStore, snapshot: FullSnapshot, model: EnergyModel | None,
                     command: DispatcherCommand, vehicle: FleetVehicle, option: ChargeOption) -> dict:
    offered_tick = (option.offered_at_tick if option.offered_at_tick is not None
                    else option.expires_at_tick - OPTION_LIFETIME_TICKS)
    response_time_s = (snapshot.tick - offered_tick) * TICK_S
    option_ids = vehicle_option_ids(snapshot, vehicle.vehicle_id)
    # A rejected command may hold any payload, so only a string option ID can name this shuttle's option.
    rejected = sum(1 for c in store.rejected_commands(snapshot.session_id, BATTERY_COMMANDS, offered_tick)
                   if c.expected_epoch == snapshot.session_epoch and isinstance(c.payload.get("option_id"), str)
                   and c.payload["option_id"] in option_ids)
    return {"option_id": option.option_id, "option_revision": option.revision,
            "vehicle_id": vehicle.vehicle_id,
            "reason": command.reason,
            "response_time_s": response_time_s,
            "out_of_service_s": option.out_of_service_s,
            "battery_after_kwh": option.battery_after_kwh,
            "loops_after": option.loops_after,
            "min_in_service_vehicles": option.min_in_service_vehicles,
            "rejected_attempts": rejected,
            "assessment": (score_charge_decision(model, vehicle, option, response_time_s, rejected)
                           if model is not None else None)}


"""
# -----------------------------------------------------------------------------------
# Function: finish_charging
# -----------------------------------------------------------------------------------
Description:
    This function ends one shuttle's charging session and frees its port,
    for the session actor (BE-M2-02) to call when the shuttle's charging
    time is up. The shuttle must be CHARGING on a port. It gains the energy
    its charger delivers in charged_s (capped at capacity), moves to
    STANDBY, and leaves the port; the shuttle's and charger's revisions rise
    by one. Offers are rebuilt with the scenario's energy model, so other
    shuttles see the freed port. The CHARGING_FINISHED event records the
    shuttle, the time on the charger, the energy added and the battery
    after charging. The caller commits both with store.commit_snapshot.

Args:
    snapshot (FullSnapshot): The current committed snapshot.
    vehicle_id (str): The shuttle that has finished charging.
    charged_s (float): Seconds the shuttle spent charging.

Returns:
    tuple[FullSnapshot, Event]: The new snapshot and its event; the input
        snapshot is unchanged.

Raises:
    TrainingStoreError: INVALID_REFERENCE for an unknown shuttle or energy
        model, INVALID_TRANSITION if the shuttle is not charging on a port.
"""
def finish_charging(snapshot: FullSnapshot, vehicle_id: str, charged_s: float) -> tuple[FullSnapshot, Event]:
    model = ENERGY_MODELS.get(snapshot.scenario.energy_model_revision)
    if model is None:
        raise TrainingStoreError("INVALID_REFERENCE", "Scenario energy model is unknown")
    vehicle = next((v for v in snapshot.vehicles if v.vehicle_id == vehicle_id), None)
    if vehicle is None:
        raise TrainingStoreError("INVALID_REFERENCE", "Shuttle does not exist")
    port = next((c for c in snapshot.chargers if vehicle_id in c.occupied_vehicle_ids), None)
    if vehicle.service_state != "CHARGING" or port is None:
        raise TrainingStoreError("INVALID_TRANSITION", "Shuttle is not charging on a port")

    states = [ChargerState(c.charger_id, c.port_count, c.occupied_vehicle_ids) for c in snapshot.chargers]
    released = release_port(states, port.charger_id, vehicle_id)
    battery_after = charge_battery_kwh(model, vehicle.battery_kwh, vehicle.battery_capacity_kwh,
                                       port.power_kw, charged_s)

    proposed = snapshot.model_copy(deep=True)
    charger = next(c for c in proposed.chargers if c.charger_id == port.charger_id)
    charger.occupied_vehicle_ids = list(next(c for c in released if c.charger_id == port.charger_id)
                                        .occupied_vehicle_ids)
    charger.aggregate_revision += 1
    shuttle = next(v for v in proposed.vehicles if v.vehicle_id == vehicle_id)
    shuttle.battery_kwh = battery_after
    shuttle.service_state = "STANDBY"
    shuttle.aggregate_revision += 1
    finish_hold(proposed, vehicle_id)
    proposed = with_battery_decisions(model, proposed)
    proposed.event_sequence += 1
    event = Event(event_id=str(uuid4()), event_sequence=proposed.event_sequence, tick=proposed.tick,
                  entity_type="CHARGER", entity_id=charger.charger_id,
                  entity_revision=charger.aggregate_revision, event_type="CHARGING_FINISHED",
                  source=proposed.source,
                  data={"vehicle_id": vehicle_id, "charged_s": charged_s,
                        "energy_added_kwh": battery_after - vehicle.battery_kwh,
                        "battery_kwh": battery_after})
    return proposed, event
