"""
Schedule, move and cancel charger bookings in a v2 training session.

Description:
    SCHEDULE_CHARGE targets a charger at its current revision and names the shuttle and start tick;
    "replaces_booking_id" moves an existing slot in one step. CANCEL_CHARGE_BOOKING targets the
    booking's charger and names the booking. As with the battery decisions, the command is
    journalled first, so a stale or invalid request still gets one durable REJECTED outcome, and
    every rule is checked again against the current snapshot.

    The session actor starts a booked slot when its time comes (IntegratedSessionActor._run_bookings).
"""
from collections.abc import Callable
from uuid import uuid4

from contracts.v2.models import (
    ChargeBooking, ChargeScheduleRequest, ChargeSchedulePreview, CommandStatus, DispatcherCommand, Event, FullSnapshot,
    validate_command_against_snapshot,
)
from src.energy.bookings import assess_slot, settle
from src.energy.decisions import with_battery_decisions
from src.energy.model import ENERGY_MODELS, EnergyModel
from src.persistence.training_store import TrainingStore, TrainingStoreError

BOOKING_COMMANDS = {"SCHEDULE_CHARGE", "CANCEL_CHARGE_BOOKING"}
DEFAULT_CHARGE_S = 1200.0  # matches with_battery_decisions for scenarios without their own charge time


def preview_slot(snapshot: FullSnapshot, request: ChargeScheduleRequest, charge_s: float = DEFAULT_CHARGE_S,
                 charge_time_scale: float | None = None) -> ChargeSchedulePreview:
    model = ENERGY_MODELS.get(snapshot.scenario.energy_model_revision)
    if model is None:
        raise TrainingStoreError("INVALID_REFERENCE", "Scenario energy model is unknown")
    if not any(v.vehicle_id == request.vehicle_id for v in snapshot.vehicles):
        raise TrainingStoreError("INVALID_REFERENCE", "Shuttle does not exist")
    if not any(c.charger_id == request.charger_id for c in snapshot.chargers):
        raise TrainingStoreError("INVALID_REFERENCE", "Charger does not exist")
    return assess_slot(model, snapshot, request, charge_s, charge_time_scale)


def _request(command: DispatcherCommand) -> ChargeScheduleRequest | None:
    payload = command.payload
    vehicle, start, replaces = payload.get("vehicle_id"), payload.get("start_tick"), payload.get("replaces_booking_id")
    if (not isinstance(vehicle, str) or not vehicle or type(start) is not int or start < 0
            or (replaces is not None and not isinstance(replaces, str))):
        return None
    return ChargeScheduleRequest(vehicle_id=vehicle, charger_id=command.target_id, start_tick=start, replaces_booking_id=replaces)


def submit_booking_command(store: TrainingStore, actor_id: str, command: DispatcherCommand, *, charge_s: float = DEFAULT_CHARGE_S,
                           charge_time_scale: float | None = None,
                           rebuild_decisions: Callable[[EnergyModel, FullSnapshot], FullSnapshot] = with_battery_decisions) -> CommandStatus:
    receipt = store.accept(actor_id, command, defer_preconditions=True)
    if receipt.status != "QUEUED":
        return receipt
    _, accepted = store.command_record(command.session_id, actor_id, command.idempotency_key)
    if accepted.command_type == "CANCEL_CHARGE_BOOKING":
        return _apply_cancel(store, accepted, rebuild_decisions)
    return _apply_schedule(store, accepted, charge_s, charge_time_scale, rebuild_decisions)


def _commit(store, command, snapshot, charger_id, event_type, data, rebuild_decisions, also_bump=None) -> CommandStatus:
    charger = next(c for c in snapshot.chargers if c.charger_id == charger_id)
    charger.aggregate_revision += 1
    if also_bump is not None and also_bump != charger_id:
        next(c for c in snapshot.chargers if c.charger_id == also_bump).aggregate_revision += 1
    model = ENERGY_MODELS.get(snapshot.scenario.energy_model_revision)
    if model is not None:
        snapshot = rebuild_decisions(model, snapshot)  # a nearby slot changes which chargers can be offered
    snapshot.event_sequence += 1
    event = Event(event_id=str(uuid4()), event_sequence=snapshot.event_sequence, tick=snapshot.tick,
                  entity_type="CHARGER", entity_id=charger_id, entity_revision=charger.aggregate_revision,
                  event_type=event_type, source=snapshot.source, data={**data, "reason": command.reason})
    return store.apply(command.command_id, snapshot, (event,))


def _apply_schedule(store, command, charge_s, charge_time_scale, rebuild_decisions) -> CommandStatus:
    snapshot = store.snapshot(command.session_id)
    failure = validate_command_against_snapshot(command, snapshot)
    if failure is not None:
        return store.apply(command.command_id, rejection_code=failure.code)
    request = _request(command)
    if request is None:
        return store.apply(command.command_id, rejection_code="INVALID_PAYLOAD")
    try:
        preview = preview_slot(snapshot, request, charge_s, charge_time_scale)
    except TrainingStoreError as error:
        return store.apply(command.command_id, rejection_code=error.code)
    blocking = next((i for i in preview.issues if i.blocking), None)
    if blocking is not None:
        return store.apply(command.command_id, rejection_code=blocking.code)

    proposed = snapshot.model_copy(deep=True)
    moved_from = None
    if request.replaces_booking_id is not None:
        replaced = next(b for b in proposed.charge_bookings if b.booking_id == request.replaces_booking_id)
        moved_from = replaced.charger_id
        settle(proposed, replaced, "CANCELLED", "RESCHEDULED")
    booking = ChargeBooking(booking_id=f"booking-{uuid4().hex}", revision=1, vehicle_id=request.vehicle_id,
                            charger_id=request.charger_id, status="SCHEDULED", origin="OPERATOR",
                            start_tick=request.start_tick, end_tick=preview.end_tick)
    proposed.charge_bookings.append(booking)
    return _commit(store, command, proposed, request.charger_id, "CHARGE_SCHEDULED",
                   {"booking_id": booking.booking_id, "vehicle_id": booking.vehicle_id, "start_tick": booking.start_tick,
                    "end_tick": booking.end_tick, "replaces_booking_id": request.replaces_booking_id,
                    "battery_after_kwh": preview.battery_after_kwh, "loops_after": preview.loops_after,
                    "min_in_service_vehicles": preview.min_in_service_vehicles},
                   rebuild_decisions, also_bump=moved_from)


def _apply_cancel(store, command, rebuild_decisions) -> CommandStatus:
    snapshot = store.snapshot(command.session_id)
    failure = validate_command_against_snapshot(command, snapshot)
    if failure is not None:
        return store.apply(command.command_id, rejection_code=failure.code)
    if snapshot.session.status != "RUNNING":
        return store.apply(command.command_id, rejection_code="INVALID_TRANSITION")
    proposed = snapshot.model_copy(deep=True)
    booking = next((b for b in proposed.charge_bookings if b.booking_id == command.payload.get("booking_id")
                    and b.charger_id == command.target_id), None)
    if booking is None:
        return store.apply(command.command_id, rejection_code="INVALID_REFERENCE")
    if booking.status != "SCHEDULED":  # an ACTIVE booking already holds the port
        return store.apply(command.command_id, rejection_code="INVALID_TRANSITION")
    settle(proposed, booking, "CANCELLED", "OPERATOR")
    return _commit(store, command, proposed, booking.charger_id, "CHARGE_BOOKING_CANCELLED",
                   {"booking_id": booking.booking_id, "vehicle_id": booking.vehicle_id}, rebuild_decisions)
