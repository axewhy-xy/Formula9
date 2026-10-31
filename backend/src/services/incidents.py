"""BE-M2-05 incident domain, driven by the fleet actor's committed clock.

No simulation loop lives here. ``publish`` wraps an actor's proposed publication;
``decide`` applies an existing DispatcherCommand against a committed checkpoint.
All option evidence, timing and provisional assessment live in the SQLite journal.
"""

from __future__ import annotations

from datetime import UTC, datetime
from copy import deepcopy
from functools import lru_cache
import json
from pathlib import Path
from uuid import uuid4

from contracts.v2.models import (
    Candidate, DispatcherCommand, Disruption, Event, FullSnapshot, Incident,
    Provenance, RouteRevision, validate_command_against_snapshot,
)
from contracts.v2.route_edit import RouteEditRequest
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.services.route_edit import RouteEditService, get_route_edit_service
from src.services.incident_scenarios.simulated_catalog import SimulatedIncidentCatalog


FIXTURE_PATH = Path(__file__).resolve().parents[2] / "fixtures" / "m2_incident.json"


class IncidentService:
    def __init__(self, routes: RouteEditService, fixture_path: Path = FIXTURE_PATH, *, fixture: dict | None = None):
        self.routes = routes
        self.config = deepcopy(fixture) if fixture is not None else json.loads(fixture_path.read_text(encoding="utf-8"))
        # Template catalog is read-only; the existing authored incident workflow is unchanged.
        self.simulated_catalog = SimulatedIncidentCatalog()

    def owns(self, snapshot: FullSnapshot) -> bool:
        return snapshot.scenario.scenario_id == self.config["scenario_id"]

    def _incident(self, snapshot: FullSnapshot, incident_id: str) -> Incident:
        if not self.owns(snapshot):
            raise TrainingStoreError("INVALID_REFERENCE", "No authored incident fixture for this target")
        incident = next((i for i in snapshot.incidents if i.incident_id == incident_id), None)
        if incident is None:
            raise TrainingStoreError("INVALID_REFERENCE", "Incident has not been detected")
        return incident

    def _source(self, snapshot: FullSnapshot, kind: str = "TRAINING_SIMULATION") -> Provenance:
        branch = snapshot.source.branch_tick
        return Provenance(kind=kind, authored=True,
                          branch_tick=min(branch, snapshot.tick) if branch is not None else snapshot.tick)

    def _event(self, snapshot: FullSnapshot, incident: Incident, event_type: str, data: dict) -> Event:
        snapshot.event_sequence += 1
        return Event(event_id=str(uuid4()), event_sequence=snapshot.event_sequence, tick=snapshot.tick,
                     entity_type="INCIDENT", entity_id=incident.incident_id,
                     entity_revision=incident.aggregate_revision, event_type=event_type,
                     source=snapshot.source, data=data)

    def _dependencies(self, snapshot: FullSnapshot, incident: Incident) -> dict:
        vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == incident.vehicle_id)
        return {
            "fixture_id": self.config["fixture_id"], "session_epoch": snapshot.session_epoch,
            "scene_revision": snapshot.scene_revision, "transform_revision": snapshot.transform_revision,
            "rules_revision": snapshot.scenario.rules_revision,
            "vehicle_model_revision": snapshot.scenario.vehicle_model_revision,
            "energy_model_revision": snapshot.scenario.energy_model_revision,
            "rubric_revision": snapshot.scenario.rubric_revision,
            "route_id": vehicle.route_id, "route_revision": vehicle.route_revision,
            "route": next(r.model_dump(mode="json") for r in snapshot.routes if r.route_id == vehicle.route_id),
            "closures": {d.disruption_id: d.model_dump(mode="json") for d in snapshot.disruptions},
        }

    def _road_preview(self, snapshot: FullSnapshot, incident: Incident):
        vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == incident.vehicle_id)
        return self.routes.preview(snapshot, RouteEditRequest(
            route_id=vehicle.route_id, expected_route_revision=vehicle.route_revision, draft_revision=1,
        ), retain=False)

    def _options(self, snapshot: FullSnapshot, incident: Incident) -> dict:
        road = self._road_preview(snapshot, incident)
        disruption = next(d for d in snapshot.disruptions if d.disruption_id == self.config["disruption_id"])
        wait_s = max(0, disruption.active_until_tick - snapshot.tick) * self.config["step_s"]
        auxiliary_kw = self.routes._fixture["auxiliary_kw"]
        baseline = road.original.model_dump(mode="json") if road.original else None
        options = {}
        for action in self.config["reference_acceptable_actions"]:
            candidate_id = f"{incident.incident_id}-r{incident.aggregate_revision}-{action.lower()}"
            delay_s = min(wait_s, self.config["escalation_after_ticks"] * self.config["step_s"]) if action == "ESCALATE" else wait_s
            if action == "ROAD_DETOUR":
                metrics = road.edited.model_dump(mode="json") if road.edited else None
                validation = road.validation
                checks = [c.model_dump(mode="json") for c in road.checks]
                delay_s = 0.0
            else:
                metrics = dict(baseline) if baseline else None
                if metrics:
                    metrics["duration_s"] += delay_s
                    metrics["energy_kwh"] += auxiliary_kw * delay_s / 3600
                # Validate the original road at the promised release time, including
                # any other closure that would still be active then.
                future = snapshot.model_copy(deep=True)
                future.tick = snapshot.tick + round(delay_s / self.config["step_s"])
                closure = next(d for d in future.disruptions if d.disruption_id == disruption.disruption_id)
                closure.active_until_tick = future.tick
                route = next(r for r in future.routes if r.route_id == self.config["route_id"])
                continuation = self.routes.preview(future, RouteEditRequest(
                    route_id=route.route_id, expected_route_revision=route.revision, draft_revision=1,
                    directed_edge_ids=route.directed_edge_ids,
                ), retain=False)
                validation = continuation.validation if metrics else "UNKNOWN"
                checks = [c.model_dump(mode="json") for c in continuation.checks]
                checks.append({"check": "SAFE_HOLD", "status": "PASS",
                               "message": "Remain stopped until the authored obstruction is cleared"})
            candidate = Candidate(
                candidate_id=candidate_id, incident_id=incident.incident_id, revision=1, action=action,
                validation=validation, expires_at_tick=snapshot.tick + self.config["option_lifetime_ticks"],
                dependency_revisions={incident.incident_id: incident.aggregate_revision},
                assumptions=[*self.config["assumptions"],
                             "Invalidated by changed obstruction, route, scene, rules, session epoch or incident state."],
            )
            snapshot.candidates.append(candidate)
            incident.candidate_ids.append(candidate_id)
            options[candidate_id] = {
                "action": action, "validation": validation, "checks": checks,
                "dependencies": self._dependencies(snapshot, incident),
                "directed_edge_ids": road.directed_edge_ids if action == "ROAD_DETOUR" else [],
                "baseline": baseline, "estimated_consequence": metrics,
                "hold_s": delay_s, "is_estimate": True,
            }
        return options

    def publish(self, store: TrainingStore, proposed: FullSnapshot) -> FullSnapshot:
        """Actor hook: hold only the affected vehicle; leave peer movement/energy intact.

        Call at each presentation boundary instead of ``store.commit_snapshot``.
        A concurrent decision makes the publication stale and must be restaged.
        """
        previous = store.snapshot(proposed.session_id)
        snapshot = proposed.model_copy(deep=True)
        if not self.owns(snapshot):
            return store.commit_snapshot(snapshot, expected_snapshot_id=previous.snapshot_id)
        if snapshot.session.status != "RUNNING" or previous.session.status != "RUNNING":
            raise TrainingStoreError("INVALID_TRANSITION", "Incident publications require a running session")
        if abs(snapshot.simulation_time_s - snapshot.tick * self.config["step_s"]) > 1e-6:
            raise TrainingStoreError("INVALID_PAYLOAD", "Incident fixture requires the 50 ms clock")
        if proposed.snapshot_id != previous.snapshot_id:
            raise TrainingStoreError("STALE_REVISION", "Actor must stage from the latest committed checkpoint")
        events = []
        incident = next((i for i in snapshot.incidents if i.incident_id == self.config["incident_id"]), None)
        vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == self.config["vehicle_id"])
        before_vehicle = next(v for v in previous.vehicles if v.vehicle_id == vehicle.vehicle_id)
        # Defer until eligible so a dwell or different route cannot stall fleet publications.
        informational = not self.config.get("requires_guidance", True)
        if (incident is None and informational and snapshot.tick >= self.config["trigger_tick"]):
            snapshot.source = self._source(snapshot)
            disruption = Disruption(
                disruption_id=self.config["disruption_id"], aggregate_revision=1,
                affected_edge_ids=self.config["affected_edge_ids"],
                active_from_tick=snapshot.tick,
                active_until_tick=snapshot.tick + self.config["clear_after_ticks"],
                reason=f"Synthetic {self.config.get('kind', 'ROAD_INCIDENT')}: network impact assessment",
                provenance=snapshot.source,
            )
            snapshot.disruptions.append(disruption)
            incident = Incident(incident_id=self.config["incident_id"], aggregate_revision=1,
                                vehicle_id=vehicle.vehicle_id, state="UNRESOLVED", detected_tick=snapshot.tick,
                                candidate_ids=[], provenance=snapshot.source)
            snapshot.incidents.append(incident)
            events.append(self._event(snapshot, incident, "INCIDENT_DETECTED", {
                "incident_type": self.config.get("kind"),
                "map_marker": self.config.get("map_marker"),
                "incident_edge_ids": self.config["affected_edge_ids"],
                "impact_edge_ids": self.config.get("impact_edge_ids", []),
                "requires_guidance": False,
                "operational_impact": "Potential traffic delay on Shuttle 01 route" if self.config.get("impact_edge_ids") else "No effect on operations — no action required",
                "reason": "Informational network incident; no operator decision required",
            }))
        if (not informational and incident is None and snapshot.tick >= self.config["trigger_tick"] and
            vehicle.route_id == self.config["route_id"] and vehicle.service_state == "IN_SERVICE"):
            snapshot.source = self._source(snapshot)
            for item in snapshot.vehicles:
                item.provenance = snapshot.source.model_copy(deep=True)
            affected_edges = self.config.get("affected_edge_ids", self.routes._fixture["closed_directed_edge_ids"])
            if self.config.get("block_current_route_entrance", False):
                affected_edges = [self.routes.first_avoidable_edge(snapshot, vehicle.route_id)]
            disruption = Disruption(
                disruption_id=self.config["disruption_id"], aggregate_revision=1,
                affected_edge_ids=affected_edges,
                active_from_tick=snapshot.tick, active_until_tick=snapshot.tick + self.config["clear_after_ticks"],
                reason="Authored road obstruction; wait, request simulated assistance, or take a validated road detour",
                provenance=snapshot.source,
            )
            snapshot.disruptions.append(disruption)
            incident = Incident(incident_id=self.config["incident_id"], aggregate_revision=2,
                                vehicle_id=vehicle.vehicle_id, state="OPTIONS_READY", detected_tick=snapshot.tick,
                                candidate_ids=[], provenance=snapshot.source)
            snapshot.incidents.append(incident)
            vehicle.service_state = "AWAITING_GUIDANCE"
            vehicle.aggregate_revision += 1
            options = self._options(snapshot, incident)
            events.append(self._event(snapshot, incident, "OPTIONS_READY", {
                "fixture_id": self.config["fixture_id"], "map_marker": self.config.get("map_marker"),
                "incident_type": self.config.get("kind"),
                "impact_edge_ids": self.config.get("impact_edge_ids", []),
                "operational_impact": "Road closure on Shuttle 01 route — operator review required",
                "detected_at_utc": datetime.now(UTC).isoformat(),
                "battery_at_detection_kwh": vehicle.battery_kwh,
                "transitions": ["DETECTED", "AWAITING_REVIEW", "OPTIONS_READY"],
                "vehicle_transitions": ["BLOCKED", "AWAITING_GUIDANCE"], "options": options,
            }))
        if incident is not None and informational:
            disruption = next((d for d in snapshot.disruptions if d.disruption_id == self.config["disruption_id"]), None)
            if (disruption is not None and disruption.active_until_tick is not None
                    and snapshot.tick >= disruption.active_until_tick and incident.state == "UNRESOLVED"):
                incident.state = "RESOLVED"
                incident.aggregate_revision += 1
                events.append(self._event(snapshot, incident, "INCIDENT_RESOLVED", {
                    "reason": "Synthetic network incident duration elapsed", "camera": None,
                }))
        if incident is not None and not informational:
            # A branch cannot regain unrelated recorded-camera continuity on a later publication.
            snapshot.source = self._source(previous if self.config["incident_id"] in {i.incident_id for i in previous.incidents} else snapshot)
            for item in snapshot.vehicles:
                item.provenance = snapshot.source.model_copy(deep=True)
            disruption = next(d for d in snapshot.disruptions if d.disruption_id == self.config["disruption_id"])
            if (not events and incident.state == "OPTIONS_READY" and
                disruption.active_until_tick is not None and snapshot.tick < disruption.active_until_tick):
                evidence = self._evidence(store, previous, incident)
                previous_options = evidence.data["options"]
                if any(option["dependencies"] != self._dependencies(snapshot, incident) for option in previous_options.values()):
                    incident.aggregate_revision += 1
                    snapshot.candidates = [c for c in snapshot.candidates if c.incident_id != incident.incident_id]
                    incident.candidate_ids = []
                    options = self._options(snapshot, incident)
                    events.append(self._event(snapshot, incident, "OPTIONS_READY", {
                        "fixture_id": self.config["fixture_id"],
                        "detected_at_utc": evidence.data["detected_at_utc"],
                        "battery_at_detection_kwh": evidence.data["battery_at_detection_kwh"],
                        "reason": "Refresh options after dependency changes", "options": options,
                    }))
            if incident.state in {"OPTIONS_READY", "WAITING", "ESCALATED"}:
                vehicle.position = before_vehicle.position.model_copy(deep=True)
                vehicle.speed_mps = 0.0
                vehicle.service_state = "AWAITING_GUIDANCE"
                if disruption.active_until_tick is not None and snapshot.tick >= disruption.active_until_tick:
                    # An unanswered incident is unresolved, even though traffic can proceed.
                    incident.state = "UNRESOLVED" if incident.state == "OPTIONS_READY" else "RESOLVED"
                    incident.aggregate_revision += 1
                    vehicle.service_state = "IN_SERVICE"
                    vehicle.aggregate_revision += 1
                    data = {"reason": "Authored obstruction cleared", "camera": None,
                            "service_state": vehicle.service_state,
                            "operational_delay_s": (snapshot.tick - incident.detected_tick) * self.config["step_s"],
                            "observed_battery_kwh": vehicle.battery_kwh}
                    evidence = self._evidence(store, previous, incident)
                    data["observed_battery_change_kwh"] = vehicle.battery_kwh - evidence.data["battery_at_detection_kwh"]
                    if incident.state == "UNRESOLVED":
                        data["assessment"] = {
                            "rubric_revision": self.config["rubric_revision"], "provisional": True,
                            "uncalibrated": True, "scope": "INCIDENT_ONLY",
                            "criteria": dict.fromkeys(self.config["rubric"], 0),
                            "maximum_points": self.config["rubric"], "total": 0,
                            "attempts": len(store.incident_attempts(snapshot.session_id, incident.incident_id)),
                            "assumptions": self.config["assumptions"],
                        }
                    events.append(self._event(snapshot, incident, "INCIDENT_" + incident.state, data))
            elif incident.state == "EXECUTING" and vehicle.service_state == "IN_SERVICE":
                incident.state = "RESOLVED"
                incident.aggregate_revision += 1
                events.append(self._event(snapshot, incident, "INCIDENT_RESOLVED",
                                          {"reason": "Actor completed the approved detour", "camera": None,
                                           "service_state": vehicle.service_state,
                                           "observed_battery_kwh": vehicle.battery_kwh,
                                           "observed_battery_change_kwh": vehicle.battery_kwh - self._evidence(store, previous, incident).data["battery_at_detection_kwh"]}))
        return store.commit_snapshot(snapshot, tuple(events), expected_snapshot_id=previous.snapshot_id)

    def _evidence(self, store: TrainingStore, snapshot: FullSnapshot, incident: Incident) -> Event:
        events = store.entity_events(snapshot.session_id, incident.incident_id, snapshot.event_sequence)
        evidence = next((e for e in reversed(events) if e.event_type == "OPTIONS_READY"), None)
        if evidence is None:
            raise TrainingStoreError("INVALID_REFERENCE", "Incident has no durable authored option evidence")
        return evidence

    def _evaluate(self, store: TrainingStore, snapshot: FullSnapshot, command: DispatcherCommand) -> tuple[FullSnapshot, dict]:
        incident = self._incident(snapshot, command.target_id)
        failure = validate_command_against_snapshot(command, snapshot)
        if failure:
            raise TrainingStoreError(failure.code, failure.message)
        if command.command_type != "APPROVE_INCIDENT_OPTION" or command.target_type != "INCIDENT":
            raise TrainingStoreError("INVALID_PAYLOAD", "Expected an incident option command")
        if snapshot.session.status != "RUNNING" or incident.state != "OPTIONS_READY":
            raise TrainingStoreError("INVALID_TRANSITION", "Incident is not awaiting a live decision")
        if not command.reason or not command.reason.strip():
            raise TrainingStoreError("INVALID_PAYLOAD", "A decision requires a nonblank reason")
        candidate_id = command.payload.get("candidate_id")
        if candidate_id not in incident.candidate_ids:
            raise TrainingStoreError("INVALID_REFERENCE", "Option is not offered by this incident")
        evidence = self._evidence(store, snapshot, incident)
        option = evidence.data["options"].get(candidate_id)
        if option is None or option["dependencies"] != self._dependencies(snapshot, incident):
            raise TrainingStoreError("STALE_REVISION", "Obstruction or route dependencies changed")
        disruption = next(d for d in snapshot.disruptions if d.disruption_id == self.config["disruption_id"])
        if not (disruption.active_from_tick <= snapshot.tick < disruption.active_until_tick):
            raise TrainingStoreError("STALE_REVISION", "Obstruction is no longer active")
        vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == incident.vehicle_id)
        if vehicle.service_state != "AWAITING_GUIDANCE" or vehicle.speed_mps != 0.0:
            raise TrainingStoreError("INVALID_TRANSITION", "Affected shuttle must be safely stopped")
        candidate = next(c for c in snapshot.candidates if c.candidate_id == candidate_id)
        if candidate.action != option["action"] or candidate.validation != option["validation"]:
            raise TrainingStoreError("STALE_REVISION", "Option evidence changed")
        result = snapshot.model_copy(deep=True)
        result.source = self._source(snapshot)
        for item in result.vehicles:
            item.provenance = result.source.model_copy(deep=True)
        target = next(i for i in result.incidents if i.incident_id == incident.incident_id)
        shuttle = next(v for v in result.vehicles if v.vehicle_id == incident.vehicle_id)
        action = option["action"]
        # Recompute wait consequences at decision time, not at option-generation time.
        consequence = dict(option["estimated_consequence"])
        if action == "ROAD_DETOUR":
            refreshed = self._road_preview(snapshot, incident)
            if not refreshed.approvable or refreshed.directed_edge_ids != option["directed_edge_ids"]:
                raise TrainingStoreError("NO_FEASIBLE_ROUTE", "Road detour no longer passes validation")
            route = next(r for r in snapshot.routes if r.route_id == shuttle.route_id)
            detour = RouteRevision(route_id=incident.incident_id + "-detour", revision=1,
                                  scene_revision=snapshot.scene_revision, directed_edge_ids=refreshed.directed_edge_ids,
                                  edge_paths_m=refreshed.edge_paths_m(),
                                  required_stop_ids=route.required_stop_ids, stops_m=route.stops_m,
                                  distance_m=refreshed.edited.distance_m,
                                  estimated_duration_s=refreshed.edited.duration_s)
            result.routes.append(detour)
            shuttle.route_id, shuttle.route_revision = detour.route_id, detour.revision
            shuttle.service_state = "DETOURING"
            target.state = "EXECUTING"
            transitions = ["ACTION_COMMITTED", "EXECUTING"]
        else:
            closure = next(d for d in result.disruptions if d.disruption_id == self.config["disruption_id"])
            if action == "ESCALATE":
                closure.active_until_tick = min(closure.active_until_tick, snapshot.tick + self.config["escalation_after_ticks"])
                closure.aggregate_revision += 1
            delay_s = (closure.active_until_tick - snapshot.tick) * self.config["step_s"]
            continuation_state = result.model_copy(deep=True)
            continuation_state.tick = closure.active_until_tick
            route = next(r for r in result.routes if r.route_id == shuttle.route_id)
            continuation = self.routes.preview(continuation_state, RouteEditRequest(
                route_id=route.route_id, expected_route_revision=route.revision, draft_revision=1,
                directed_edge_ids=route.directed_edge_ids,
            ), retain=False)
            if not continuation.approvable:
                raise TrainingStoreError("NO_FEASIBLE_ROUTE", "Safe continuation after the hold is no longer feasible")
            consequence = dict(option["baseline"])
            consequence["duration_s"] += delay_s
            consequence["energy_kwh"] += self.routes._fixture["auxiliary_kw"] * delay_s / 3600
            target.state = "WAITING" if action == "WAIT" else "ESCALATED"
            transitions = [target.state]
        target.aggregate_revision += 1
        shuttle.aggregate_revision += 1
        response_s = round(snapshot.simulation_time_s - incident.detected_tick * self.config["step_s"], 6)
        return result, {
            "command_id": command.command_id, "candidate_id": candidate_id, "action": action,
            "reason": command.reason, "transitions": transitions,
            "response_time_s": response_s,
            "timing_basis": "SERVER_SIMULATION_CLOCK_EXCLUDES_PAUSES",
            "estimated_consequence": consequence, "baseline": option["baseline"], "is_estimate": True,
            "estimated_additional_service_delay_s": round(response_s + consequence["duration_s"] - option["baseline"]["duration_s"], 6),
            "estimated_additional_energy_kwh": round(self.routes._fixture["auxiliary_kw"] * response_s / 3600 +
                                                     consequence["energy_kwh"] - option["baseline"]["energy_kwh"], 6),
            "rule_outcomes": option["checks"] if action == "ROAD_DETOUR" else [c.model_dump(mode="json") for c in continuation.checks],
            "energy_model_revision": snapshot.scenario.energy_model_revision,
            "consequence_model": "m2_route_detour.json declared comparison parameters",
        }

    def _score(self, decision: dict, attempts: int) -> dict:
        limits = self.config["rubric"]
        elapsed = decision["response_time_s"]
        time_fraction = max(0.0, min(1.0, (self.config["zero_time_credit_s"] - elapsed) /
                                    (self.config["zero_time_credit_s"] - self.config["full_time_credit_s"])))
        consequence, baseline = decision["estimated_consequence"], decision["baseline"]
        criteria = {
            "safety_compliance": limits["safety_compliance"],
            "response_time": round(limits["response_time"] * time_fraction, 2),
            "attempts": max(0, limits["attempts"] - 2 * (attempts - 1)),
            "service_coverage": limits["service_coverage"] if all(consequence["stop_coverage"].values()) else 0,
            "route_efficiency": round(limits["route_efficiency"] * min(1.0, baseline["duration_s"] /
                                                                         max(consequence["duration_s"] + elapsed, 0.001)), 2),
        }
        return {"rubric_revision": self.config["rubric_revision"], "provisional": True,
                "scope": "INCIDENT_ONLY", "uncalibrated": True, "criteria": criteria,
                "maximum_points": limits, "total": round(sum(criteria.values()), 2),
                "attempts": attempts, "assumptions": self.config["assumptions"]}

    def decide(self, store: TrainingStore, actor_id: str, command: DispatcherCommand):
        snapshot = store.snapshot(command.session_id)
        self._incident(snapshot, command.target_id)
        # Journal denied attempts too; apply always rechecks the canonical preconditions.
        receipt = store.accept(actor_id, command, defer_preconditions=True)
        if receipt.status != "QUEUED":
            return receipt
        _, accepted = store.command_record(command.session_id, actor_id, command.idempotency_key)
        snapshot = store.snapshot(command.session_id)
        try:
            proposed, data = self._evaluate(store, snapshot, accepted)
        except TrainingStoreError as error:
            return store.apply(accepted.command_id, rejection_code=error.code)
        attempts = store.incident_attempts(snapshot.session_id, accepted.target_id)
        evidence = self._evidence(store, snapshot, self._incident(snapshot, accepted.target_id))
        record = next(a for a in attempts if a["command_id"] == accepted.command_id)
        data["wall_response_time_s"] = max(0.0, (datetime.fromisoformat(record["accepted_at_utc"]) -
                                               datetime.fromisoformat(evidence.data["detected_at_utc"])).total_seconds())
        attempt_count = next(index + 1 for index, attempt in enumerate(attempts) if attempt["command_id"] == accepted.command_id)
        data["assessment"] = self._score(data, attempt_count)
        event = self._event(proposed, self._incident(proposed, accepted.target_id), "INCIDENT_DECISION_RECORDED", data)
        return store.apply(accepted.command_id, proposed, (event,), expected_snapshot_id=snapshot.snapshot_id)

    def preview(self, store: TrainingStore, session_id: str, command: DispatcherCommand) -> dict:
        if command.session_id != session_id:
            raise TrainingStoreError("INVALID_REFERENCE", "Command session does not match URL")
        snapshot = store.snapshot(session_id)  # A durable checkpoint, never a mutable actor buffer.
        proposed, data = self._evaluate(store, snapshot, command)
        proposed.snapshot_id = str(uuid4())
        return {"branch_id": proposed.snapshot_id, "base_checkpoint_id": snapshot.snapshot_id,
                "base_tick": snapshot.tick, "base_event_sequence": snapshot.event_sequence,
                "source": proposed.source.model_dump(mode="json"), "camera": None,
                "camera_unavailable_reason": "Authored simulation alternative has no matched recorded camera",
                "snapshot": proposed.model_dump(mode="json"), "decision": data}

    def history(self, store: TrainingStore, session_id: str, incident_id: str) -> dict:
        live = store.snapshot(session_id)
        incident = self._incident(live, incident_id)
        end = incident.detected_tick
        requested_start = end - self.config["history_ticks"]
        samples = store.history(session_id, max(0, requested_start), end)
        complete = bool(samples) and requested_start >= 0 and samples[0].tick == requested_start and samples[-1].tick == end
        complete = complete and all(b.tick - a.tick <= self.config["maximum_history_gap_ticks"] for a, b in zip(samples, samples[1:]))
        for sample in samples:
            sample.source = Provenance(kind="HISTORICAL_SIMULATION", authored=True, branch_tick=sample.source.branch_tick)
            for vehicle in sample.vehicles:
                vehicle.provenance = sample.source.model_copy(deep=True)
        return {"incident_id": incident_id, "source_mode": "HISTORICAL_SIMULATION",
                "requested_duration_s": 10.0, "complete": complete, "camera": None,
                "unavailable_reason": None if complete else "Insufficient committed simulation prehistory or a sampling gap",
                "snapshots": [s.model_dump(mode="json") for s in samples]}

    def debrief(self, store: TrainingStore, session_id: str, incident_id: str) -> dict:
        snapshot = store.snapshot(session_id)
        incident = self._incident(snapshot, incident_id)
        events = store.entity_events(session_id, incident_id, snapshot.event_sequence)
        decisions = [e.data for e in events if e.event_type == "INCIDENT_DECISION_RECORDED"]
        assessments = [e.data["assessment"] for e in events if "assessment" in e.data]
        outcomes = [e.data for e in events if e.event_type in {"INCIDENT_RESOLVED", "INCIDENT_UNRESOLVED"}]
        return {"incident_id": incident_id, "state": incident.state, "provisional": True,
                "rubric_revision": self.config["rubric_revision"],
                "assessment": assessments[-1] if assessments else None,
                "outcome": outcomes[-1] if outcomes else None,
                "reference_acceptable_actions": self.config["reference_acceptable_actions"],
                "attempts": store.incident_attempts(session_id, incident_id),
                "decisions": decisions, "events": [e.model_dump(mode="json") for e in events]}


@lru_cache
def get_incident_service() -> IncidentService:
    return IncidentService(get_route_edit_service())
