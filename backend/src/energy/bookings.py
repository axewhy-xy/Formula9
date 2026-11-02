"""
Time-slot charger bookings for the fleet training simulation.

Description:
    The rules for holding a charger port over a window of simulation ticks. assess_slot judges a
    requested slot, and is the one check behind both the preview the operator sees and the command
    that commits the slot, so the two cannot disagree. The other functions change a snapshot's
    bookings as ports are taken and given back.

    Windows are estimates (travel, charging, then clearing the bay). A port is only ever occupied
    through the charger's occupied_vehicle_ids; a booking alone never occupies a bay.
"""
import math

from contracts.v2.models import (
    ChargeBooking, ChargeScheduleIssue, ChargeSchedulePreview, ChargeScheduleRequest, FullSnapshot,
    transition_allowed,
)
from .decisions import IN_SERVICE_STATES
from .forecast import loop_energy_kwh, loops_remaining, reserve_kwh
from .model import EnergyModel, charge_battery_kwh, seconds_to_full

TICK_S = 0.05
LIVE = {"SCHEDULED", "ACTIVE"}
PARKING_ALLOWANCE_S = 60.0     # time to drive clear of the bay after charging
MAX_HORIZON_TICKS = 72000      # one simulated hour ahead
KEPT_FINISHED_BOOKINGS = 20    # finished bookings stay visible; the event log keeps them all


def live_bookings(snapshot: FullSnapshot) -> list[ChargeBooking]:
    return [b for b in snapshot.charge_bookings if b.status in LIVE]


def overlaps(booking: ChargeBooking, start_tick: int, end_tick: int) -> bool:
    return booking.start_tick < end_tick and start_tick < booking.end_tick


def slot_ticks(eta_s: float, charge_s: float) -> int:
    return math.ceil((eta_s + charge_s + PARKING_ALLOWANCE_S) / TICK_S)


def assess_slot(model: EnergyModel, snapshot: FullSnapshot, request: ChargeScheduleRequest,
                charge_s: float, charge_time_scale: float | None = None) -> ChargeSchedulePreview:
    """Judge a requested slot against the current snapshot. Reserves nothing.

    The vehicle and charger must exist; the caller checks that first. With charge_time_scale the
    shuttle charges to full, so the slot's length follows the battery it will arrive with.
    """
    vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == request.vehicle_id)
    charger = next(c for c in snapshot.chargers if c.charger_id == request.charger_id)
    forecast = next((f for f in snapshot.energy_forecasts if f.vehicle_id == vehicle.vehicle_id), None)
    route = next((r for r in snapshot.routes if r.route_id == vehicle.route_id), None)
    eta_s = (forecast.next_depot_eta_s or 0.0) if forecast is not None else 0.0
    # What it will have by then sets how long the slot lasts, so moving the slot can change its length
    serving = vehicle.service_state in IN_SERVICE_STATES
    wait_s = max(0, request.start_tick - snapshot.tick) * TICK_S
    drain_kw = 0.0
    if serving and route is not None and route.estimated_duration_s > 0:
        drain_kw = loop_energy_kwh(model, route.distance_m, route.estimated_duration_s) / route.estimated_duration_s
    at_start = max(0.0, vehicle.battery_kwh - drain_kw * wait_s)
    arrival = max(0.0, at_start - drain_kw * eta_s)
    if charge_time_scale:
        charge_s = seconds_to_full(model, arrival, vehicle.battery_capacity_kwh, charger.power_kw, charge_time_scale)
    end_tick = request.start_tick + slot_ticks(eta_s, charge_s)
    issues: list[ChargeScheduleIssue] = []

    def issue(code: str, message: str, blocking: bool = True) -> None:
        issues.append(ChargeScheduleIssue(code=code, message=message, blocking=blocking))

    if snapshot.session.status != "RUNNING":
        issue("INVALID_TRANSITION", "The session is not running, so charging cannot be scheduled.")
    if request.start_tick < snapshot.tick:
        issue("INVALID_PAYLOAD", "This slot has already started. Choose a later time.")
    if request.start_tick > snapshot.tick + MAX_HORIZON_TICKS:
        issue("INVALID_PAYLOAD", "Slots can be booked up to one hour ahead.")

    replaced = next((b for b in snapshot.charge_bookings if b.booking_id == request.replaces_booking_id), None)
    if request.replaces_booking_id is not None and (
            replaced is None or replaced.status != "SCHEDULED" or replaced.vehicle_id != vehicle.vehicle_id):
        issue("INVALID_REFERENCE", "That booking can no longer be moved.")
    if any(b.vehicle_id == vehicle.vehicle_id and b.booking_id != request.replaces_booking_id
           for b in live_bookings(snapshot)):
        issue("CHARGER_CONFLICT", f"{vehicle.vehicle_id} already has a charging booking.")

    if vehicle.service_state == "OUT_OF_SERVICE":
        issue("INVALID_TRANSITION", f"{vehicle.vehicle_id} is out of service and cannot drive to a charger.")
    elif not transition_allowed("vehicle", vehicle.service_state, "RETURNING_TO_DEPOT"):
        issue("VEHICLE_NOT_READY", f"{vehicle.vehicle_id} cannot leave for the depot right now; "
                                   "the slot waits until it can.", blocking=False)

    # A shuttle already on its way back to its route has given the port back
    booked = [b for b in live_bookings(snapshot) if b.charger_id == charger.charger_id and b.phase != "RETURNING_TO_ROUTE"
              and b.vehicle_id != vehicle.vehicle_id and overlaps(b, request.start_tick, end_tick)]
    if len(booked) >= charger.port_count:
        issue("CHARGER_CONFLICT", f"{charger.charger_id} is booked by "
                                  f"{', '.join(sorted(b.vehicle_id for b in booked))} at this time.")
    # A shuttle on the port with no booking (an older session) has no known end, so count it for one charge
    unbooked = [v for v in charger.occupied_vehicle_ids if v != vehicle.vehicle_id
                and not any(b.vehicle_id == v and b.charger_id == charger.charger_id for b in live_bookings(snapshot))]
    if unbooked and request.start_tick < snapshot.tick + math.ceil(charge_s / TICK_S) and len(unbooked) + len(booked) >= charger.port_count:
        issue("CHARGER_CONFLICT", f"{charger.charger_id} is in use by {', '.join(sorted(unbooked))}.")

    reserve = reserve_kwh(model, vehicle.battery_capacity_kwh)
    if forecast is None:
        issue("NO_FORECAST", "No battery forecast is available for this shuttle.", blocking=False)
    elif serving and arrival < reserve:
        issue("NO_FEASIBLE_ROUTE", f"{vehicle.vehicle_id} would reach the depot below its {reserve:g} kWh reserve "
                                   "if it waits for this slot. Choose an earlier time.")

    after = charge_battery_kwh(model, arrival, vehicle.battery_capacity_kwh, charger.power_kw * (charge_time_scale or 1.0), charge_s)
    loops_after = None
    if route is not None:
        loops_after = loops_remaining(model, after, vehicle.battery_capacity_kwh,
                                      loop_energy_kwh(model, route.distance_m, route.estimated_duration_s))
    away = {b.vehicle_id for b in live_bookings(snapshot) if overlaps(b, request.start_tick, end_tick)}
    still_serving = {v.vehicle_id for v in snapshot.vehicles if v.service_state in IN_SERVICE_STATES}
    remaining = len(still_serving - away - {vehicle.vehicle_id})
    if remaining == 0:
        issue("NO_SERVICE_COVER", "No shuttle would be left in service during this slot.", blocking=False)

    validation = "FAIL" if any(i.blocking for i in issues) else "WARN" if issues else "PASS"
    return ChargeSchedulePreview(
        session_id=snapshot.session_id, tick=snapshot.tick, vehicle_id=vehicle.vehicle_id,
        charger_id=charger.charger_id, start_tick=request.start_tick, end_tick=end_tick,
        replaces_booking_id=request.replaces_booking_id, validation=validation, issues=issues,
        charger_revision=charger.aggregate_revision, out_of_service_s=(end_tick - request.start_tick) * TICK_S,
        battery_at_start_kwh=at_start, battery_after_kwh=after, loops_after=loops_after,
        min_in_service_vehicles=remaining,
        assumptions=[f"Leaves for the depot at the slot start; return estimated at {eta_s:.0f} s from where it is now",
                     f"Charges for {charge_s:.0f} s" + (f" to full, at {charge_time_scale:g}x real time for training" if charge_time_scale else "")
                     + f", then {PARKING_ALLOWANCE_S:g} s to clear the bay",
                     "Battery use while waiting follows the shuttle's normal loop"])


def _prune(snapshot: FullSnapshot) -> None:
    finished = [b for b in snapshot.charge_bookings if b.status not in LIVE]
    drop = {b.booking_id for b in finished[:-KEPT_FINISHED_BOOKINGS]} if len(finished) > KEPT_FINISHED_BOOKINGS else set()
    snapshot.charge_bookings = [b for b in snapshot.charge_bookings if b.booking_id not in drop]


def settle(snapshot: FullSnapshot, booking: ChargeBooking, status: str, reason: str | None = None) -> None:
    """Moves a booking in this snapshot to a new status."""
    booking.status, booking.status_reason = status, reason
    booking.phase, booking.trip_path_m, booking.set_down_stop_id = None, [], None
    booking.revision += 1
    _prune(snapshot)


def record_hold(snapshot: FullSnapshot, vehicle_id: str, charger_id: str, tick: int, duration_s: float, origin: str) -> ChargeBooking:
    """A shuttle has just taken a port without a slot (CHARGE_NOW or a safety return).

    The hold joins the schedule as an ACTIVE booking, and any slot the shuttle had booked is
    replaced by it.
    """
    for booking in live_bookings(snapshot):
        if booking.vehicle_id == vehicle_id:
            settle(snapshot, booking, "CANCELLED", "SUPERSEDED_BY_" + origin)
    hold = ChargeBooking(booking_id=f"booking-{vehicle_id}-{origin.lower().replace('_', '-')}-{tick}", revision=1,
                         vehicle_id=vehicle_id, charger_id=charger_id, status="ACTIVE", origin=origin,
                         start_tick=tick, end_tick=tick + max(1, math.ceil(duration_s / TICK_S)))
    snapshot.charge_bookings.append(hold)
    return hold


def finish_hold(snapshot: FullSnapshot, vehicle_id: str, status: str = "COMPLETED", reason: str | None = None) -> None:
    """The shuttle has given its port back, so its ACTIVE booking is final."""
    for booking in live_bookings(snapshot):
        if booking.vehicle_id == vehicle_id and booking.status == "ACTIVE":
            settle(snapshot, booking, status, reason)
