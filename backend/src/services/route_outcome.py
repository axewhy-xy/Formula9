"""What an approved route actually did, from the session's durable events.

The actor records each stop served and each loop finished. This reads them back against what
the operator approved, so the report compares the decision's estimate with what happened. It only
reads: nothing here affects a running exercise.
"""

from __future__ import annotations

from contracts.v2.models import FullSnapshot
from contracts.v2.route_edit import RouteMetrics, RouteOutcome, RouteOutcomeLoop, RouteOutcomeStop
from src.energy.forecast import reserve_kwh
from src.energy.model import ENERGY_R1
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.services.route_edit import stop_label

REPLAY_PAGE = 500


def _events(store: TrainingStore, session_id: str, upto: int) -> list:
    events, cursor = [], 0
    while cursor < upto:
        page = store.replay(session_id, cursor, REPLAY_PAGE)
        if not page.events:
            break
        events.extend(page.events)
        cursor = page.events[-1].event_sequence
    return events


def route_outcome(store: TrainingStore, session_id: str, route_id: str) -> RouteOutcome:
    snapshot: FullSnapshot = store.snapshot(session_id)
    route = next((r for r in snapshot.routes if r.route_id == route_id), None)
    if route is None:
        raise TrainingStoreError("INVALID_REFERENCE", "Route was not found")
    events = _events(store, session_id, snapshot.event_sequence)
    # A shuttle re-routed at an incident is still running the decision approved for it before the trip
    riders = {v.vehicle_id for v in snapshot.vehicles if v.route_id == route_id}
    approval = next((e for e in reversed(events) if e.event_type == "ROUTE_APPROVED" and
                     (e.entity_id == route_id or riders.intersection(e.data.get("target_vehicle_ids") or []))), None)
    outcome = RouteOutcome(session_id=session_id, route_id=route_id, route_revision=route.revision, state="NOT_APPROVED")
    if approval is None:
        return outcome
    data = approval.data
    planned = RouteMetrics.model_validate(data["edited"]) if data.get("edited") else None
    original = RouteMetrics.model_validate(data["original"]) if data.get("original") else None
    targets = data.get("target_vehicle_ids") or []
    vehicle = next((v for v in snapshot.vehicles if v.vehicle_id in targets), None)
    outcome.state = "APPROVED"
    outcome.vehicle_id = vehicle.vehicle_id if vehicle else None
    outcome.reason = next((e.data.get("reason") for e in [approval] if e.data.get("reason")), None)
    outcome.planned, outcome.original = planned, original
    if vehicle is None:
        return outcome

    if vehicle.battery_kwh is not None:
        outcome.battery_kwh = vehicle.battery_kwh
        if planned and planned.energy_kwh > 0:
            spare = vehicle.battery_kwh - reserve_kwh(ENERGY_R1, vehicle.battery_capacity_kwh)
            outcome.loops_left_on_charge = max(0, int(spare // planned.energy_kwh))
    if snapshot.session.status != "READY":
        outcome.state = "RUNNING"

    # Only what happened after this approval, by this shuttle
    after = [e for e in events if e.event_sequence > approval.event_sequence and e.entity_id == vehicle.vehicle_id]
    served: dict[str, int] = {stop: 0 for stop in route.required_stop_ids}
    this_loop: set[str] = set()
    loops: list[RouteOutcomeLoop] = []
    for event in after:
        if event.event_type == "STOP_SERVED" and event.data.get("stop_id") in served:
            served[event.data["stop_id"]] += 1
            this_loop.add(event.data["stop_id"])
        elif event.event_type == "LOOP_COMPLETED":
            loops.append(RouteOutcomeLoop(duration_s=event.data["duration_s"], energy_kwh=event.data["energy_kwh"],
                                          all_stops_served=this_loop >= set(served), finished_tick=event.tick))
            this_loop = set()
    outcome.stops = [RouteOutcomeStop(stop_id=stop, name=stop_label(stop), times_served=count, served_this_loop=stop in this_loop)
                     for stop, count in served.items()]
    outcome.loops = loops
    outcome.held_for_incident = vehicle.service_state == "AWAITING_GUIDANCE"
    outcome.summary = _summary(outcome, loops, planned, original, vehicle.vehicle_id)
    return outcome


def _summary(outcome: RouteOutcome, loops: list[RouteOutcomeLoop], planned, original, vehicle_id: str) -> list[str]:
    lines = []
    if outcome.held_for_incident:
        lines.append(f"{vehicle_id} is stopped, waiting for a decision on an incident.")
    if not loops:
        return lines
    outcome.state = "LOOP_COMPLETED"
    last = loops[-1]
    outcome.service_preserved = all(loop.all_stops_served for loop in loops)
    if planned:
        outcome.delay_vs_plan_s = round(last.duration_s - planned.duration_s, 3)
        outcome.extra_energy_vs_plan_kwh = round(last.energy_kwh - planned.energy_kwh, 6)
    if original:
        outcome.delay_vs_original_s = round(last.duration_s - original.duration_s, 3)
        outcome.extra_energy_vs_original_kwh = round(last.energy_kwh - original.energy_kwh, 6)
    lines.append("Every required stop was served on every loop." if outcome.service_preserved
                 else "A required stop was missed on at least one loop.")
    if outcome.delay_vs_plan_s is not None and abs(outcome.delay_vs_plan_s) >= 5:
        lines.append(f"The last loop ran {abs(outcome.delay_vs_plan_s):.0f} s {'slower' if outcome.delay_vs_plan_s > 0 else 'faster'} than planned; "
                     "traffic holds and incidents account for the difference.")
    return lines
