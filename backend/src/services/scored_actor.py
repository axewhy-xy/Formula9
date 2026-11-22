"""The Toa Payoh assessment scenario and its observed outcome evidence.

Author: Nur Syazliyarni Binte Zazali
Date Created: 15/11/2026
Last Modified: 23/11/2026

Uses the same traffic, commands and validation as the integrated exercise.
Only the scored scenario sets a fixed route closure, designated incident,
charging shuttle and end time. Its observation state follows the actor's
checkpoint, so recovery preserves minima and opportunity windows.
"""

from copy import copy
from dataclasses import asdict, dataclass
from hashlib import sha256
import json

from uuid import uuid4

from contracts.v2.models import Disruption, Event, FullSnapshot
from src.persistence.training_store import TrainingStoreError
from contracts.v2.route_edit import RouteEditRequest
from src.energy.decisions import IN_SERVICE_STATES
from src.energy.forecast import reserve_kwh
from src.energy.model import ENERGY_R1
from src.services.assessment import AssessmentPlan, Rubric, SCORED_INCIDENT
from src.services.integrated_actor import EPSILON, SHUTTLE_FRONT_M, IntegratedSessionActor

PRETRIP_CLOSURE = "assessment-pretrip-closure"


@dataclass
class Observations:
    """Scalar observations copied independently for each staged tick."""

    route_loop_finished: bool = False
    charging_offer_tick: int | None = None
    usable_charge_since: int | None = None
    charging_actionable: bool = False
    minimum_kwh: float | None = None
    minimum_in_service: int | None = None
    below_reserve: bool = False
    target_booking_id: str | None = None
    charging_finished: bool = False
    assessment_stop_served: bool = False
    incident_service_m: float = 0.0
    trip_booking_id: str | None = None
    trip_m: float = 0.0


class ScoredSessionActor(IntegratedSessionActor):
    """A fixed route, incident and charging exercise in the integrated simulator."""

    @classmethod
    def from_checkpoint(cls, session_id: str, state: dict):
        """Restore scenario-owned data without exposing its observation fields to the session host."""
        plan = AssessmentPlan.model_validate(state["assessment_plan"])
        actor = cls(plan.rubric, session_id=session_id, plan=plan)
        actor.observations = Observations(**state["assessment_observations"])
        return actor

    def __init__(self, rubric: Rubric, *, session_id: str, plan: AssessmentPlan | None = None):
        super().__init__(seed=rubric.seed, session_id=session_id)
        self.observations = Observations()
        # Keep earlier pinned identities reproducible. New identities include behavioral settings.
        excluded = {"metadata"} if rubric.identity_version == "semantic-v1" else set()
        identity = json.dumps(rubric.model_dump(mode="json", exclude_none=True, exclude=excluded), sort_keys=True)
        self.scenario_revision = "scored-toa-payoh-" + sha256(f"{self.scenario_revision}:{identity}".encode()).hexdigest()[:24]
        self.domain.scenario.revision_id = self.domain.session.scenario_revision_id = self.scenario_revision
        self.domain.scenario.assessment_duration_s = rubric.exercise_limit_s
        self.graded_route = next(s for s in self.shuttles if self.domain.vehicles[s.ID].vehicle_id == rubric.route_vehicle_id).ID
        self.graded_charge = next(s for s in self.shuttles if self.domain.vehicles[s.ID].vehicle_id == rubric.charging_vehicle_id).ID
        if self.graded_route == self.graded_charge or self.domain.vehicles[self.graded_route].route_id != rubric.route_id:
            raise ValueError("The graded route and charging tasks must name distinct shuttles on the assigned routes")
        self._graded_shuttle_ids = tuple(sorted((self.graded_route, self.graded_charge)))
        short = rubric.short_exercise
        if short:
            # The stop approach lies on the common section of the original and approved routes.
            shuttle = self.shuttles[self.graded_route]
            stop_positions = self.stop_names[shuttle.route_ID]
            at = next((distance for distance, stop in stop_positions.items() if stop == short.stop_id), None)
            if at is None:
                raise ValueError("The designated assessment stop must be on the graded route")
            distance = self._routes[shuttle.route_ID]["total_length"] if at == "end" else float(at)
            shuttle.progress = max(0, distance - short.approach_m)
            shuttle.position = self._route_evaluation(shuttle.route_ID, shuttle.progress)
        for shuttle in self.shuttles:
            # Only the designated shuttle begins near its low-battery decision threshold.
            if shuttle.ID == self.graded_charge or shuttle.battery < 50:
                level = rubric.charging_initial_kwh if shuttle.ID == self.graded_charge else 28.0
                shuttle.battery = level * 100 / self.BATTERY_CAPACITY_KWH
        routes = self.definition.routes.for_route(self.domain, rubric.route_id)
        route = next(route for route in self.domain.routes if route.route_id == rubric.route_id)
        config = routes.route_config(rubric.route_id)
        blocked = config["closed_directed_edge_ids"]
        self.domain.disruptions.append(Disruption(disruption_id=PRETRIP_CLOSURE, aggregate_revision=1,
            affected_edge_ids=blocked, active_from_tick=0, reason="Pre-trip assessment closure", provenance=self.domain.source))
        if plan is None:
            reference = routes.preview(self.domain, RouteEditRequest(route_id=route.route_id,
                expected_route_revision=route.revision, draft_revision=1,
                directed_edge_ids=config["detour_directed_edge_ids"]), retain=False)
            if not reference.approvable or not reference.edited or not route.required_stop_ids:
                raise ValueError("The assessment requires a feasible reference detour and required stops")
            plan = AssessmentPlan(rubric=rubric, required_stops=route.required_stop_ids,
                reference_duration_s=reference.edited.duration_s, reference_energy_kwh=reference.edited.energy_kwh,
                step_s=self.STEP_S)
        self.assessment_plan = plan
        self._end_tick = plan.ticks(rubric.exercise_limit_s)
        self._charge_window_ticks = plan.ticks(rubric.charging_observation_s)
        self._safety_opportunity_ticks = plan.ticks(rubric.safety_opportunity_s)
        self._reserve_kwh = reserve_kwh(ENERGY_R1, self.BATTERY_CAPACITY_KWH)
        follow_through = rubric.rules.get("charging_follow_through")
        self._target_kwh = (self.BATTERY_CAPACITY_KWH * follow_through.charge_target_fraction
                            if follow_through is not None and short is None else None)
        self._active_closures = ()
        self._closure_crossings = {}
        self.charge_offer_limits = {rubric.charging_vehicle_id: rubric.charging_offer_loops}
        self.domain = self.generate_snapshot()

    def __deepcopy__(self, memo):
        """Reuse read-only assessment data and detach the scalar observations."""
        memo[id(self.assessment_plan)] = self.assessment_plan
        memo[id(self.observations)] = copy(self.observations)
        memo[id(self._graded_shuttle_ids)] = self._graded_shuttle_ids
        memo[id(self._active_closures)] = self._active_closures
        memo[id(self._closure_crossings)] = self._closure_crossings
        return super().__deepcopy__(memo)

    def checkpoint_extension(self) -> dict:
        return {"assessment_plan": self.assessment_plan.model_dump(mode="json"),
                "assessment_observations": asdict(self.observations)}

    def admission_options(self) -> dict:
        return {"assessment_plan": self.assessment_plan}

    def publication_due(self) -> bool:
        return super().publication_due() or self.assessment_due

    def before_publication(self, snapshot, events):
        """End the exercise at its pinned boundary while keeping normal session commits generic."""
        if self.assessment_due and snapshot.session.status == "RUNNING":
            snapshot.session.status = "COMPLETED"
            snapshot.session.aggregate_revision += 1
            completed = Event(event_id=uuid4().hex, event_sequence=0, tick=snapshot.tick,
                entity_type="SESSION", entity_id=snapshot.session_id, entity_revision=snapshot.session.aggregate_revision,
                event_type="SESSION_COMPLETED", source=snapshot.source, data={"status": "COMPLETED"})
            return (*events, completed)
        return events

    @property
    def assessment_due(self):
        """Finish on the last published boundary of the exercise's active simulation time."""
        return self.current_tick >= self._end_tick

    def _note_loop_finished(self, shuttle):
        complete = shuttle.ID == self.graded_route and shuttle.ID in self.loop_started
        super()._note_loop_finished(shuttle)
        if complete and not self.assessment_plan.rubric.short_exercise and not self.observations.route_loop_finished:
            self.observations.route_loop_finished = True
            self._queue_assessment_incident()

    def _note_stop(self, shuttle):
        pending_before = len(self.pending_events)
        super()._note_stop(shuttle)
        short = self.assessment_plan.rubric.short_exercise
        if (short and shuttle.ID == self.graded_route and not self.observations.assessment_stop_served
                and len(self.pending_events) > pending_before
                and self.pending_events[-1].data.get("stop_id") == short.stop_id):
            self.observations.assessment_stop_served = True
            self._queue_assessment_incident()

    def _queue_assessment_incident(self):
        """Let the new pose publish before the incident can hold the shuttle."""
        self.incident_due_tick = self.current_tick + self.assessment_plan.ticks(
            self.assessment_plan.rubric.incident_start_delay_s)

    def _schedule_timeline(self):
        """The scored exercise has its own incident and no ambient scripted events."""

    def _schedule_incident(self):
        """Use the existing guided closure placement after the designated route milestone."""
        if (self.observations.route_loop_finished or self.observations.assessment_stop_served) and self.random_incident is None:
            super()._schedule_incident()

    def _incident_opening(self):
        rubric = self.assessment_plan.rubric
        return {"kind": "ROAD_BLOCK", "duration_s": rubric.incident_duration_s,
                "event_id": SCORED_INCIDENT, "vehicle_id": rubric.route_vehicle_id, "minimum_distance_m": 35}

    def observe_motion(self, proposed):
        """Check actual front crossings using one active closure set per step."""
        tick = self.current_tick
        pretrip = next((d for d in self.domain.disruptions if d.disruption_id == PRETRIP_CLOSURE
                        and d.active_from_tick <= tick and (d.active_until_tick is None or tick < d.active_until_tick)), None)
        closures = ((tuple(pretrip.affected_edge_ids), pretrip.active_from_tick, None),) if pretrip else ()
        if self.random_incidents:
            closures += tuple((tuple(record["affected_edge_ids"]), record["event"]["start_tick"], SCORED_INCIDENT)
                              for record, _ in self._live_events(tick) if record["incident_id"] == SCORED_INCIDENT)
        if closures != self._active_closures:
            self._active_closures = closures
            self._closure_crossings = {}
        short = self.assessment_plan.rubric.short_exercise
        if not closures and not short:
            return
        shuttles = self.shuttles if closures else (self.shuttles[i] for i in self._graded_shuttle_ids)
        for shuttle in shuttles:
            destination = proposed[shuttle.ID]
            if destination <= shuttle.progress:
                continue
            if short and shuttle.ID in self._graded_shuttle_ids:
                self._observe_milestone_progress(shuttle, destination - shuttle.progress, short)
            if not closures:
                continue
            before, after = shuttle.progress + SHUTTLE_FRONT_M, destination + SHUTTLE_FRONT_M
            for edge, start, start_tick, incident_id in self._closed_entries(shuttle.route_ID):
                if before < start <= after:
                    self._event(shuttle, "ASSESSMENT_CLOSED_ROAD_ENTRY", edge_id=edge, route_id=shuttle.route_ID,
                                crossing_tick=tick, closed_from_tick=start_tick, incident_id=incident_id)

    def _closed_entries(self, route_id):
        """Cache the closed-road boundaries until the path or closure set changes."""
        path = self._routes[route_id]
        cached = self._closure_crossings.get(route_id)
        if cached is not None and cached[0] is path:
            return cached[1]
        entries = tuple((edge, start, start_tick, incident_id) for edge, start, _ in self._route_roads(route_id)
                        for affected, start_tick, incident_id in self._active_closures if edge in affected)
        # Staged actors share the old mapping. Replace it so a discarded tick cannot change its source.
        self._closure_crossings = {**self._closure_crossings, route_id: (path, entries)}
        return entries

    def _observe_milestone_progress(self, shuttle, distance_m, short):
        """Measure post-decision movement using the applied route and booking."""
        evidence = self.observations
        if shuttle.ID == self.graded_route and self.random_incident and evidence.incident_service_m < short.incident_service_m:
            vehicle_id = self.assessment_plan.rubric.route_vehicle_id
            incident = next((item for item in self.domain.incidents if item.incident_id == SCORED_INCIDENT), None)
            response = next((item for item in incident.responses if item.vehicle_id == vehicle_id), None) if incident else None
            mode = self.modes[shuttle.ID]
            if (response and response.action != "NO_DECISION" and mode in IN_SERVICE_STATES
                    and (mode == "DETOURING" or self.current_tick >= self._incident_window(self.random_incident)[1])):
                evidence.incident_service_m += distance_m
                if evidence.incident_service_m >= short.incident_service_m:
                    self._event(shuttle, "ASSESSMENT_SERVICE_RESUMED",
                                distance_m=short.incident_service_m, observed_tick=self.current_tick)
        if (shuttle.ID == self.graded_charge and not evidence.charging_finished
                and self.modes[shuttle.ID] == "RETURNING_TO_DEPOT" and shuttle.ID not in self.parking_chargers):
            vehicle_id = self.assessment_plan.rubric.charging_vehicle_id
            booking = next((item for item in self.domain.charge_bookings
                            if item.vehicle_id == vehicle_id and item.status == "ACTIVE" and item.origin != "SAFETY_RETURN"), None)
            if booking:
                if evidence.trip_booking_id != booking.booking_id:
                    evidence.trip_booking_id, evidence.trip_m = booking.booking_id, 0.0
                if evidence.trip_m < short.charging_trip_m:
                    evidence.trip_m += distance_m
                    if evidence.trip_m >= short.charging_trip_m:
                        self._event(shuttle, "ASSESSMENT_CHARGING_TRIP_MOVED", booking_id=booking.booking_id,
                                    distance_m=short.charging_trip_m)

    def tick(self):
        """Observe minima on each movement tick, including dips between public snapshots."""
        super().tick()
        if not self.is_paused:
            self._observe_charging()

    def observe_publication(self, snapshot: FullSnapshot, events):
        """Record only offers that were actually published to the trainee or acted on."""
        if snapshot.session.status not in {"RUNNING", "COMPLETED"}:
            return
        shuttle = self.shuttles[self.graded_charge]
        evidence = self.observations
        if evidence.charging_actionable:
            self._observe_charging()
            return
        rubric = self.assessment_plan.rubric
        option = next((o for o in snapshot.charge_options if o.vehicle_id == rubric.charging_vehicle_id
                       and o.action == "CHARGE_NOW" and o.validation == "PASS"), None)
        usable = option is not None and any(f.vehicle_id == rubric.charging_vehicle_id and f.reaches_depot_above_reserve
                                            for f in snapshot.energy_forecasts)
        if evidence.charging_offer_tick is None:
            booking = next((e for e in events if e.event_type == "CHARGE_SCHEDULED"
                            and e.data.get("vehicle_id") == rubric.charging_vehicle_id), None)
            existing_booking = next((b for b in snapshot.charge_bookings
                                     if b.vehicle_id == rubric.charging_vehicle_id
                                     and b.status in {"SCHEDULED", "ACTIVE"} and b.origin != "SAFETY_RETURN"), None)
            continued = rubric.charging_vehicle_id in snapshot.charge_decision_deferrals
            if not usable and booking is None and existing_booking is None and not continued:
                if rubric.charging_offer_deadline_s is not None and self.current_tick == 0:
                    raise TrainingStoreError("ASSESSMENT_NOT_READY",
                        "The assessment cannot start while its charging task is unavailable. Create a fresh assessment.")
                return
            evidence.charging_offer_tick = self.current_tick
            self._event(shuttle, "ASSESSMENT_CHARGING_OFFERED", option_id=option.option_id if usable else None,
                        booking_id=booking.data["booking_id"] if booking else existing_booking.booking_id if existing_booking else None)
        if usable:
            if evidence.usable_charge_since is None:
                evidence.usable_charge_since = self.current_tick
            if self.current_tick - evidence.usable_charge_since >= self._safety_opportunity_ticks:
                evidence.charging_actionable = True
                self._event(shuttle, "ASSESSMENT_CHARGING_ACTIONABLE", first_usable_tick=evidence.usable_charge_since,
                            option_id=option.option_id)
        else:
            evidence.usable_charge_since = None
        self._observe_charging()

    def _observe_charging(self):
        """Track the charging shuttle's battery floor and the active fleet count."""
        evidence = self.observations
        if evidence.charging_offer_tick is None or evidence.charging_finished:
            return
        shuttle = self.shuttles[self.graded_charge]
        capacity = self.BATTERY_CAPACITY_KWH
        reserve = self._reserve_kwh
        level = shuttle.battery * capacity / 100
        serving = sum(mode in IN_SERVICE_STATES for mode in self.modes.values())
        evidence.minimum_kwh = level if evidence.minimum_kwh is None else min(evidence.minimum_kwh, level)
        evidence.minimum_in_service = serving if evidence.minimum_in_service is None else min(evidence.minimum_in_service, serving)
        if level < reserve - EPSILON and not evidence.below_reserve:
            self._event(shuttle, "ASSESSMENT_BELOW_RESERVE", battery_kwh=level, reserve_kwh=reserve)
        evidence.below_reserve = level < reserve - EPSILON
        if self._target_kwh is not None and level >= self._target_kwh and self.modes[shuttle.ID] == "CHARGING":
            vehicle_id = self.domain.vehicles[shuttle.ID].vehicle_id
            booking = next((b for b in self.domain.charge_bookings if b.vehicle_id == vehicle_id and b.status == "ACTIVE"), None)
            if booking is not None and booking.booking_id != evidence.target_booking_id:
                evidence.target_booking_id = booking.booking_id
                self._event(shuttle, "ASSESSMENT_CHARGE_TARGET_REACHED", booking_id=booking.booking_id, battery_kwh=level)
        if self.current_tick >= evidence.charging_offer_tick + self._charge_window_ticks:
            evidence.charging_finished = True
            self._event(shuttle, "ASSESSMENT_CHARGING_OBSERVED", minimum_kwh=evidence.minimum_kwh,
                        minimum_in_service=evidence.minimum_in_service, reserve_kwh=reserve, capacity_kwh=capacity)
