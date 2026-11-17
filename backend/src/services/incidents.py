"""BE-M2-05 incident domain, driven by the fleet actor's committed clock.

No simulation loop lives here. ``publish`` wraps an actor's proposed publication;
``decide`` applies an existing DispatcherCommand against a committed checkpoint.
All option evidence, timing and provisional assessment live in the SQLite journal.

An incident is a world event; the operator decides what each shuttle that will reach it does. Every exposed
shuttle (and the shuttle a closure stopped, when one did) is offered its own options: keep its route, take a
detour round the event, request assistance (once per event, which shortens it) or hold at its next stop until
the event clears. Each decision is a response of that shuttle (Incident.responses), scored on its own: how
soon it came, whether it came before the shuttle reached the event, and how much delay it avoided against
keeping the route. A shuttle that drives into the event with no decision has made one, and scores zero.
"""

from __future__ import annotations

from datetime import UTC, datetime
from copy import deepcopy
from functools import lru_cache
import json
from pathlib import Path
import re
from uuid import uuid4

from contracts.v2.models import (
    Candidate, DispatcherCommand, Disruption, Event, FullSnapshot, Incident, IncidentExposure, IncidentLocation,
    IncidentResponse, Provenance, RouteRevision, validate_command_against_snapshot,
)
from contracts.v2.route_edit import RouteEditRequest
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.services.route_edit import RouteEditService, get_route_edit_service, scene_closed
from src.services.incident_scenarios.simulated_catalog import SimulatedIncidentCatalog


FIXTURE_PATH = Path(__file__).resolve().parents[2] / "fixtures" / "m2_incident.json"
# A shuttle in one of these has left service for a charger (or run flat) and will not finish a detour
LEFT_SERVICE = {"RETURNING_TO_DEPOT", "QUEUED_FOR_CHARGE", "CHARGING", "STANDBY", "OUT_OF_SERVICE"}
# What every incident of a scenario shares: its clock, option lifetimes, rubric and assumptions
RULE_FIELDS = ("scenario_id", "fixture_id", "step_s", "escalation_after_ticks", "option_lifetime_ticks",
               "history_ticks", "maximum_history_gap_ticks", "reference_acceptable_actions",
               "rubric_revision", "rubric", "full_time_credit_s", "zero_time_credit_s", "assumptions")
# What one incident says about itself. Nothing else may come from its record, and nothing of the
# scenario's authored incident (its id, shuttle, trigger or closure) reaches it.
RECORD_FIELDS = frozenset({
    "incident_id", "disruption_id", "kind", "vehicle_id", "route_id", "road_name", "map_marker",
    "affected_edge_ids", "impact_edge_ids", "congested_edge_ids", "requires_guidance", "speed_factor",
    "block_current_route_entrance", "trigger_tick", "clear_after_ticks", "warning_m", "assumptions",
    # The world event it stands for, the shuttles that will reach it (refreshed by the actor) and the roads
    # its scene and queue cover
    "event", "exposures", "footprint_edge_ids",
    # Whether a decision holds its shuttle where it was when the incident was detected (a session without
    # background traffic), rather than letting it drive on until the closure stops it
    "holds_vehicle",
})
# Where a shuttle may be when its operator decides: on its route, driving, held up, at a stop or detouring
# round another incident. The shuttle a closure stopped for a decision may also be standing there.
ON_ITS_ROUTE = {"IN_SERVICE", "BLOCKED", "DWELLING"}
DECIDABLE = {*ON_ITS_ROUTE, "DETOURING"}
SETTLED = {"RESOLVED", "UNRESOLVED"}
# How an alert names each world event type, and the type of a record from before world events
EVENT_NAMES = {"BREAKDOWN": "a breakdown", "COLLISION": "a collision", "SIGNAL_FAULT": "a signal fault",
               "ROAD_CLOSURE": "a road closure"}
LEGACY_TYPES = {"ROAD_ACCIDENT": "COLLISION", "ROAD_BLOCK": "ROAD_CLOSURE"}
# The decision each state of an incident from before per-shuttle decisions stood for
LEGACY_RESPONSES = {"WAITING": "KEEP_ROUTE", "ESCALATED": "ESCALATE", "EXECUTING": "ROAD_DETOUR"}
# Every option an incident can offer one shuttle, in the order they are offered. WAIT is KEEP_ROUTE's name
# in fixtures and journals from before per-shuttle decisions.
INCIDENT_ACTIONS = ("KEEP_ROUTE", "ROAD_DETOUR", "ESCALATE", "HOLD_AT_STOP")
KEEPS_ITS_ROUTE = {"KEEP_ROUTE", "WAIT"}
# A decision taken after its shuttle has reached the incident earns this share of the response-time points
LATE_RESPONSE_SHARE = 0.5
# Delays within this of each other are the same
DELAY_EPSILON_S = 0.5
# A record that never triggers: for an incident whose record has left the checkpoint
NEVER = 10**12
# The dependencies of each issue of options, read from its evidence (IncidentService._offered_dependencies)
_EVIDENCE_DEPENDENCIES: dict[tuple, dict] = {}


def incident_config(rules: dict, record: dict) -> dict:
    """One incident's config: the scenario's shared rules and the incident's own record, and nothing else."""
    unknown = set(record) - RECORD_FIELDS
    if unknown:
        raise ValueError(f"Incident record has unknown fields: {sorted(unknown)}")
    config = {key: deepcopy(rules[key]) for key in RULE_FIELDS if key in rules}
    config.update(deepcopy(record))
    config.setdefault("block_current_route_entrance", False)
    config.setdefault("trigger_tick", NEVER)
    return config


def vehicle_label(vehicle_id: str | None) -> str:
    """How an alert names a shuttle: shuttle-03 is Shuttle 03."""
    if not vehicle_id:
        return "the shuttle"
    return " ".join(part.capitalize() if part.isalpha() else part for part in vehicle_id.split("-"))


def event_phrase(event_type: str | None, road_name: str | None) -> str:
    """An event and where it is, as an alert says it: "a collision on Lorong 6 Toa Payoh", "a signal fault
    at the junction of Braddell Road and Lorong 6 Toa Payoh"."""
    name = EVENT_NAMES.get(event_type or "", "an incident")
    return f"{name} {'at' if event_type == 'SIGNAL_FAULT' else 'on'} {road_name}" if road_name else name


def exposure_headline(event_type: str | None, road_name: str | None, exposures) -> str:
    """The alert for a world event: who reaches it first and when, not which shuttle it was placed for.

    "Shuttle 03 reaches a collision on Lorong 6 Toa Payoh in about 85 s (and 1 more shuttle)".
    """
    phrase = event_phrase(event_type, road_name)
    if not exposures:
        return f"{phrase[0].upper()}{phrase[1:]}; no shuttle reaches it before it clears"
    first = exposures[0]
    eta = round(first["eta_s"])
    when = "now" if eta <= 0 else f"in about {eta} s"
    more = len(exposures) - 1
    others = f" (and {more} more shuttle{'s' if more > 1 else ''})" if more else ""
    return f"{vehicle_label(first['vehicle_id'])} reaches {phrase} {when}{others}"


def option_id(incident_id: str, revision: int, vehicle_id: str, action: str) -> str:
    """The id of one shuttle's option, as issued at an incident revision: <incident>-r<revision>-<shuttle>-<action>."""
    return f"{incident_id}-r{revision}-{vehicle_id}-{action.lower()}"


def option_vehicle(incident_id: str, candidate_id: str) -> str | None:
    """The shuttle an option id (option_id) names, or None for one that names none: an id from before
    per-shuttle options (<incident>-r<revision>-<action>) or one this incident never issued.

    It is read from between the incident's own id and a known action, so an incident or shuttle id that
    contains the other's cannot be mistaken for it."""
    actions = "|".join(re.escape(a.lower()) for a in (*INCIDENT_ACTIONS, "WAIT"))
    match = re.fullmatch(rf"{re.escape(incident_id)}-r\d+-(.+)-(?:{actions})", candidate_id)
    return match.group(1) if match else None


def detour_route_id(incident_id: str, vehicle_id: str) -> str:
    """The route one shuttle drives round an incident. Each shuttle detouring round it has its own."""
    return f"{incident_id}-detour-{vehicle_id}"


def is_detour_of(route_id: str | None, incident_id: str) -> bool:
    """Whether a route is a detour round an incident: a shuttle's own, or the one detour of a checkpoint
    from before per-shuttle decisions."""
    return bool(route_id) and (route_id == incident_id + "-detour" or route_id.startswith(incident_id + "-detour-"))


def detouring(incidents, vehicle_id: str) -> bool:
    """Whether a shuttle is driving a detour it was sent on round an incident that has not yet ended."""
    return any(r.vehicle_id == vehicle_id and r.action == "ROAD_DETOUR" and r.completed_tick is None
               for i in incidents if i.state not in SETTLED for r in i.responses)


def holding_at_stop(incidents, vehicle_id: str, tick: int, stop_id: str | None) -> bool:
    """Whether a shuttle standing at a stop (stop_id) was told to hold there for an incident that has still to
    clear. A hold that names no stop holds it at whichever it stands at."""
    return any(r.vehicle_id == vehicle_id and r.action == "HOLD_AT_STOP" and r.completed_tick is None
               and r.hold_stop_id in (None, stop_id) and (i.clears_at_tick is None or tick < i.clears_at_tick)
               for i in incidents if i.state not in SETTLED for r in i.responses)


def response_share(fraction: float, before_arrival: bool) -> float:
    """The share of the response-time points a decision earns: by how soon it came, and only half of it
    once its shuttle has reached the incident."""
    return fraction if before_arrival else fraction * LATE_RESPONSE_SHARE


def delay_share(keep_s: float, chosen_s: float, best_s: float) -> float:
    """The share of the delay-avoided points a decision earns: all of them for the least delay any offered
    option gave, none for no less delay than keeping the route would have, and in proportion between."""
    if chosen_s <= best_s + DELAY_EPSILON_S:
        return 1.0
    if keep_s - best_s <= DELAY_EPSILON_S:
        return 0.0
    return max(0.0, min(1.0, (keep_s - chosen_s) / (keep_s - best_s)))


class IncidentService:
    def __init__(self, routes: RouteEditService, fixture_path: Path = FIXTURE_PATH, *, fixture: dict | None = None):
        self.routes = routes
        self.config = deepcopy(fixture) if fixture is not None else json.loads(fixture_path.read_text(encoding="utf-8"))
        # Template catalog is read-only; the existing authored incident workflow is unchanged.
        self.simulated_catalog = SimulatedIncidentCatalog()

    def owns(self, snapshot: FullSnapshot) -> bool:
        return snapshot.scenario.scenario_id == self.config["scenario_id"]

    def for_incident(self, store: TrainingStore, snapshot: FullSnapshot, incident_id: str) -> IncidentService:
        """Resolve the same checkpointed instance for previews, history and decisions."""
        if incident_id == self.config["incident_id"]:
            return self
        incident = next((i for i in snapshot.incidents if i.incident_id == incident_id), None)
        if not self.owns(snapshot) or incident is None:
            raise TrainingStoreError("INVALID_REFERENCE", "Incident has not been detected")
        _, state = store.runtime(snapshot.session_id)
        workflow = state.get("workflow", {})
        selections = workflow.get("random_incidents") or [workflow.get("random_incident")]
        selection = next((item for item in selections if item and item["incident_id"] == incident_id), None)
        if selection is None:
            # Settled incidents leave the checkpoint. Their evidence reads use the
            # event log and shared rubric, not the per-incident selection.
            if incident.state not in SETTLED:
                raise TrainingStoreError("INVALID_REFERENCE", "Incident configuration is unavailable")
            selection = {"incident_id": incident_id, "vehicle_id": incident.vehicle_id}
        return self.instance(selection)

    def instance(self, record: dict) -> IncidentService:
        """The service for one incident, from this scenario's rules and the incident's own record."""
        return IncidentService(self.routes, fixture=incident_config(self.config, record))

    # ----- What the incident is -----

    def _impact(self, guidance: bool) -> str:
        """What an incident means for operations, naming the shuttle it is about and the road. A world
        event that belongs to no shuttle names those that will reach it."""
        road = f" at {self.config['road_name']}" if self.config.get("road_name") else ""
        if not self.config.get("vehicle_id"):
            exposed = [vehicle_label(e["vehicle_id"]) for e in self._exposures()]
            if exposed:
                return f"Potential traffic delay for {', '.join(exposed)}{road}"
            return "No effect on operations — no shuttle reaches it before it clears"
        label = vehicle_label(self.config.get("vehicle_id"))
        if guidance:
            return f"Road closure on {label}'s route{road} — operator review required"
        if self.config.get("impact_edge_ids"):
            return f"Potential traffic delay on {label}'s route{road}"
        return "No effect on operations — no action required"

    def _guided(self) -> bool:
        """Whether the incident is a closure on the route of the shuttle it names, which must be decided
        for whether or not the actor finds it exposed (the authored scenario has no actor to)."""
        return bool(self.config.get("requires_guidance", True))

    def _holds_vehicle(self) -> bool:
        """Whether a decision holds its shuttle where it was detected; a record from before traffic played
        incidents out does."""
        return self.config.get("holds_vehicle", True)

    def _held(self, incident: Incident) -> str | None:
        """The shuttle held where the incident was detected until it is decided for and clears, or None."""
        return incident.vehicle_id if self._guided() and self._holds_vehicle() else None

    def _event_type(self) -> str | None:
        """The world event's type; a record from before world events has its kind's."""
        event = self.config.get("event") or {}
        return event.get("event_type") or LEGACY_TYPES.get(self.config.get("kind") or "")

    def _location(self) -> IncidentLocation | None:
        event = self.config.get("event") or {}
        edge = event.get("edge_id") or next(iter(self.config.get("affected_edge_ids") or ()), None)
        node = event.get("node_id")
        if edge is None and node is None:
            return None
        return IncidentLocation(edge_id=None if node is not None else edge, node_id=node,
                                road_name=(self.config.get("road_name") or "")[:512] or None)

    def _exposures(self) -> list[dict]:
        return list(self.config.get("exposures") or ())

    def _exposure(self, vehicle_id: str) -> dict | None:
        return next((e for e in self._exposures() if e["vehicle_id"] == vehicle_id), None)

    def _world(self) -> dict:
        """What every first event of an incident says about the world event behind it."""
        exposures = self._exposures()
        location = self._location()
        return {"event_type": self._event_type(),
                "location": location.model_dump(mode="json") if location else None,
                "footprint_edge_ids": list(self.config.get("footprint_edge_ids") or ()),
                "exposures": [self._public_exposure(e) for e in exposures],
                "headline": exposure_headline(self._event_type(), self.config.get("road_name"), exposures)}

    @staticmethod
    def _public_exposure(exposure: dict) -> dict:
        """An exposure as the contract has it: the actor's record also notes the stop it could hold at."""
        public = {"vehicle_id": exposure["vehicle_id"], "distance_m": float(exposure["distance_m"]),
                  "eta_s": float(exposure["eta_s"]), "is_estimate": True}
        if exposure.get("keep_route_delay_s") is not None:
            public["keep_route_delay_s"] = float(exposure["keep_route_delay_s"])
        return public

    def _disruption(self, snapshot: FullSnapshot) -> Disruption | None:
        return next((d for d in snapshot.disruptions if d.disruption_id == self.config.get("disruption_id")), None)

    def _clears_at(self, snapshot: FullSnapshot, incident: Incident) -> int:
        """When the event clears: when its closure ends, which an escalation may have brought forward, else
        when the incident says (or, for one from before it said, when its record does)."""
        closure = self._disruption(snapshot)
        if closure is not None and closure.active_until_tick is not None:
            return closure.active_until_tick
        if incident.clears_at_tick is not None:
            return incident.clears_at_tick
        return incident.detected_tick + self.config.get("clear_after_ticks", 0)

    # ----- Exposure -----

    def _sync_exposure(self, snapshot: FullSnapshot, incident: Incident) -> list[Event]:
        """Publishes the shuttles the incident will reach, as the actor last worked them out. A settled
        incident reaches no one. A change to who is exposed (not to their ETAs) is an event of its own."""
        settled = incident.state in SETTLED
        before = list(incident.exposed_vehicle_ids)
        self._publish_exposure(snapshot, incident)
        ids = incident.exposed_vehicle_ids
        if settled or set(ids) == set(before):
            return []
        exposures = incident.exposures
        data = [e.model_dump(mode="json") for e in exposures]
        return [self._event(snapshot, incident, "INCIDENT_EXPOSURE_CHANGED", {
            "exposures": data, "added_vehicle_ids": [v for v in ids if v not in before],
            "removed_vehicle_ids": [v for v in before if v not in ids],
            "headline": exposure_headline(self._event_type(), self.config.get("road_name"), data),
        })]

    def _publish_exposure(self, snapshot: FullSnapshot, incident: Incident) -> None:
        """Sets the incident's exposed shuttles from its record; none once it has settled."""
        vehicles = {v.vehicle_id for v in snapshot.vehicles}
        settled = incident.state in SETTLED
        exposures = [] if settled else [IncidentExposure(**self._public_exposure(e))
                                        for e in self._exposures() if e["vehicle_id"] in vehicles]
        incident.exposures, incident.exposed_vehicle_ids = exposures, [e.vehicle_id for e in exposures]

    # ----- Who is to be decided for -----

    def _pending(self, snapshot: FullSnapshot, incident: Incident) -> list[str]:
        """The shuttles still to be decided for, nearest first: every exposed shuttle, and the one a closure
        on its route stopped, with no decision yet that can still act on one."""
        decided = {r.vehicle_id for r in incident.responses if r.action != "NO_DECISION"}
        vehicles = {v.vehicle_id: v for v in snapshot.vehicles}
        held = self._held(incident)
        ids = list(incident.exposed_vehicle_ids)
        if self._guided() and incident.vehicle_id and incident.vehicle_id not in ids:
            ids.insert(0, incident.vehicle_id)
        pending = []
        for vehicle_id in ids:
            vehicle = vehicles.get(vehicle_id)
            if vehicle is None or vehicle_id in decided:
                continue
            if vehicle.service_state in DECIDABLE or (vehicle_id == held and vehicle.service_state == "AWAITING_GUIDANCE"):
                pending.append(vehicle_id)
        return pending

    def _escalated(self, incident: Incident) -> bool:
        return any(r.action == "ESCALATE" for r in incident.responses)

    def _summary_state(self, incident: Incident, pending: list[str]) -> str:
        """The live incident's state, from its shuttles' decisions."""
        if pending:
            return "OPTIONS_READY"
        if any(r.action == "ROAD_DETOUR" and r.completed_tick is None for r in incident.responses):
            return "EXECUTING"
        if self._escalated(incident):
            return "ESCALATED"
        if any(r.action != "NO_DECISION" for r in incident.responses):
            return "WAITING"
        return "DETECTED"

    def _upgrade_legacy(self, incident: Incident) -> None:
        """An incident from before per-shuttle decisions that its shuttle was decided for has that decision
        as its response, so it is not asked for again."""
        if incident.state == "UNRESOLVED" and not self._guided() and not incident.responses:
            # An informational incident was UNRESOLVED while it lasted; it resolves when it clears
            incident.state = "DETECTED"
            return
        action = LEGACY_RESPONSES.get(incident.state)
        if action and not incident.responses and incident.vehicle_id:
            incident.responses.append(IncidentResponse(vehicle_id=incident.vehicle_id, action=action,
                                                       decided_tick=incident.detected_tick, before_arrival=True))

    # ----- Building blocks -----

    def _new_incident(self, snapshot: FullSnapshot, *, revision: int, vehicle_id: str | None, state: str,
                      clears_at_tick: int) -> Incident:
        """A newly detected incident, with the world event behind it and who it reaches. Its first event
        carries the exposure, so no change event follows."""
        incident = Incident(incident_id=self.config["incident_id"], aggregate_revision=revision,
                            vehicle_id=vehicle_id, state=state, detected_tick=snapshot.tick,
                            candidate_ids=[], provenance=snapshot.source, event_type=self._event_type(),
                            location=self._location(), clears_at_tick=clears_at_tick)
        self._publish_exposure(snapshot, incident)
        return incident

    def _incident(self, snapshot: FullSnapshot, incident_id: str) -> Incident:
        if not self.owns(snapshot) or incident_id != self.config["incident_id"]:
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

    def _dependencies(self, snapshot: FullSnapshot, vehicle_id: str) -> dict:
        """What one shuttle's options were worked out from; any change makes them stale."""
        vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == vehicle_id)
        return {
            "fixture_id": self.config["fixture_id"], "session_epoch": snapshot.session_epoch,
            "scene_revision": snapshot.scene_revision, "transform_revision": snapshot.transform_revision,
            "rules_revision": snapshot.scenario.rules_revision,
            "vehicle_model_revision": snapshot.scenario.vehicle_model_revision,
            "energy_model_revision": snapshot.scenario.energy_model_revision,
            "rubric_revision": snapshot.scenario.rubric_revision,
            "vehicle_id": vehicle_id, "route_id": vehicle.route_id, "route_revision": vehicle.route_revision,
            "route": next((r.model_dump(mode="json") for r in snapshot.routes if r.route_id == vehicle.route_id), None),
            "closures": {d.disruption_id: d.model_dump(mode="json") for d in snapshot.disruptions},
        }

    def _offered_dependencies(self, store: TrainingStore, snapshot: FullSnapshot, incident: Incident,
                              live: list[Candidate]) -> dict:
        """What each shuttle's live options were worked out from, by shuttle, from their journalled evidence.

        Evidence never changes once written, so it is read once per issue of options (they are named by
        the incident revision they were issued at) and remembered. Crash recovery replays from a checkpoint
        and can issue the same ids again from different evidence; it starts a new session epoch, so the
        epoch is part of the key."""
        key = (str(getattr(store, "path", id(store))), snapshot.session_id, snapshot.session_epoch,
               incident.incident_id, tuple(sorted(c.candidate_id for c in live)))
        known = _EVIDENCE_DEPENDENCIES.get(key)
        if known is None:
            options = self._evidence(store, snapshot, incident).data["options"]
            known = {}
            for candidate in live:
                option = options.get(candidate.candidate_id)
                vehicle_id = candidate.vehicle_id or incident.vehicle_id
                known[vehicle_id] = option["dependencies"] if option is not None else None
            if len(_EVIDENCE_DEPENDENCIES) >= 256:
                _EVIDENCE_DEPENDENCIES.pop(next(iter(_EVIDENCE_DEPENDENCIES)))
            _EVIDENCE_DEPENDENCIES[key] = known
        return known

    def _detour_capable(self, snapshot: FullSnapshot, vehicle_id: str) -> bool:
        """Whether the route editor can take a shuttle round the incident: it drives a route the editor has
        operating metadata for, or a detour already taken from one."""
        vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == vehicle_id)
        return self.routes.base_route_id(snapshot, vehicle.route_id) is not None

    def _route_service(self, snapshot: FullSnapshot, vehicle_id: str) -> RouteEditService:
        """The route editor for a shuttle's route; incident detours retain the operating metadata of their
        original route."""
        vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == vehicle_id)
        return self.routes.for_route(snapshot, vehicle.route_id)

    def _avoiding(self, snapshot: FullSnapshot, vehicle_id: str) -> FullSnapshot:
        """The snapshot a detour round the incident is planned in: a breakdown's or a collision's road closed,
        as a closure's already is, unless the shuttle has driven onto it (as the route editor's drafts are,
        route_edit.scene_closed)."""
        incident = next((i for i in snapshot.incidents if i.incident_id == self.config["incident_id"]), None)
        vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == vehicle_id)
        route = next((r for r in snapshot.routes if r.route_id == vehicle.route_id), None)
        if incident is None or route is None:
            return snapshot
        return scene_closed(snapshot, incident, self.routes._driven_prefix(vehicle, route))

    def _road_preview(self, snapshot: FullSnapshot, vehicle_id: str):
        return self._route_service(snapshot, vehicle_id).continuation_preview(self._avoiding(snapshot, vehicle_id),
                                                                              vehicle_id, live=snapshot)

    def _delays(self, snapshot: FullSnapshot, incident: Incident, vehicle_id: str) -> dict:
        """About how long each option but a detour holds one shuttle up from now, from what the actor last
        estimated keeping its route would cost it (the queue's length at the speed in the queue, or the wait
        for a closure or a stuck light) and when the event clears.

        A shuttle held where the incident was detected waits there for the event to clear. Assistance shortens
        the wait to no more than escalation_after_ticks; holding at the next stop waits there until it clears
        instead of in the queue."""
        step = self.config["step_s"]
        left_s = max(0.0, (self._clears_at(snapshot, incident) - snapshot.tick) * step)
        escalated_s = min(left_s, self.config["escalation_after_ticks"] * step)
        exposure = self._exposure(vehicle_id)
        held = vehicle_id == self._held(incident)
        if held or exposure is None:
            # Stopped at the closure (or, in the authored scenario, about to be): it waits for it to clear
            return {"KEEP_ROUTE": left_s, "ESCALATE": escalated_s, "HOLD_AT_STOP": left_s}
        eta = float(exposure["eta_s"])
        keep = float(exposure.get("keep_route_delay_s") or 0.0)
        delays = {"KEEP_ROUTE": keep, "ESCALATE": min(keep, max(0.0, escalated_s - eta))}
        stop = exposure.get("hold_stop")
        if stop:
            delays["HOLD_AT_STOP"] = max(0.0, left_s - float(stop["eta_s"]))
        return delays

    # ----- Options -----

    def _options(self, snapshot: FullSnapshot, incident: Incident, vehicle_ids: list[str]) -> dict:
        """Every option of each of these shuttles, as candidates on the snapshot and as their evidence."""
        options = {}
        for vehicle_id in vehicle_ids:
            options.update(self._vehicle_options(snapshot, incident, vehicle_id))
        return options

    def _vehicle_options(self, snapshot: FullSnapshot, incident: Incident, vehicle_id: str) -> dict:
        step = self.config["step_s"]
        vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == vehicle_id)
        exposure = self._exposure(vehicle_id)
        held = vehicle_id == self._held(incident) and vehicle.service_state == "AWAITING_GUIDANCE"
        capable = self._detour_capable(snapshot, vehicle_id)
        closure = self._disruption(snapshot)
        delays = self._delays(snapshot, incident, vehicle_id)
        allowed = [("KEEP_ROUTE" if action == "WAIT" else action) for action in self.config["reference_acceptable_actions"]]
        auxiliary_kw = self.routes._fixture["auxiliary_kw"]
        # A signal fault holds up every approach to its junction, which the planner does not know of: a shuttle
        # keeps its route, holds or gets assistance
        detours = capable and "ROAD_DETOUR" in allowed and self._event_type() != "SIGNAL_FAULT"
        road = self._road_preview(snapshot, vehicle_id) if detours else None
        baseline = road.original.model_dump(mode="json") if road is not None and road.original else None
        offered = []
        for action in INCIDENT_ACTIONS:
            if action not in allowed:
                continue
            if action == "ROAD_DETOUR" and road is None:
                continue  # the shuttle's route is not one the route editor can take round it
            if action == "ESCALATE" and self._escalated(incident):
                continue  # assistance is requested once per event
            if action == "HOLD_AT_STOP" and (held or not (exposure and exposure.get("hold_stop"))):
                continue  # only a shuttle with a stop before the incident can hold at it
            offered.append(action)
        options = {}
        for action in offered:
            candidate_id = option_id(incident.incident_id, incident.aggregate_revision, vehicle_id, action)
            directed = []
            if action == "ROAD_DETOUR":
                metrics = road.edited.model_dump(mode="json") if road.edited else None
                validation = road.validation
                checks = [c.model_dump(mode="json") for c in road.checks]
                directed = road.directed_edge_ids
                delay_s = (max(0.0, metrics["duration_s"] - baseline["duration_s"]) if metrics and baseline else 0.0)
            else:
                delay_s = delays.get(action, 0.0)
                metrics = dict(baseline) if baseline else None
                if metrics:
                    metrics["duration_s"] += delay_s
                    metrics["energy_kwh"] += auxiliary_kw * delay_s / 3600
                checks = []
                validation = "PASS"
                if capable and closure is not None and action in {"KEEP_ROUTE", "ESCALATE"} and vehicle.route_id:
                    # Validate the original road at the promised release time, including any other closure
                    # that would still be active then.
                    future = snapshot.model_copy(deep=True)
                    future.tick = snapshot.tick + round(delays[action] / step)
                    next(d for d in future.disruptions if d.disruption_id == closure.disruption_id).active_until_tick = future.tick
                    route = next(r for r in future.routes if r.route_id == vehicle.route_id)
                    continuation = self._route_service(future, vehicle_id).preview(future, RouteEditRequest(
                        route_id=route.route_id, expected_route_revision=route.revision, draft_revision=1,
                        directed_edge_ids=route.directed_edge_ids,
                    ), retain=False)
                    validation = continuation.validation if metrics else "UNKNOWN"
                    checks = [c.model_dump(mode="json") for c in continuation.checks]
                checks.append({"check": "SAFE_HOLD" if held or action == "HOLD_AT_STOP" else "KEEP_ROUTE",
                               "status": "PASS", "message": self._option_note(action, held, exposure)})
            candidate = Candidate(
                candidate_id=candidate_id, incident_id=incident.incident_id, revision=1, action=action,
                validation=validation, expires_at_tick=snapshot.tick + self.config["option_lifetime_ticks"],
                dependency_revisions={incident.incident_id: incident.aggregate_revision},
                assumptions=[*self.config["assumptions"],
                             "Delays are estimates: the queue's length at the speed in it, or the wait for the event to clear.",
                             "Invalidated by changed obstruction, route, scene, rules, session epoch or incident state."],
                vehicle_id=vehicle_id,
            )
            snapshot.candidates.append(candidate)
            incident.candidate_ids.append(candidate_id)
            options[candidate_id] = {
                "action": action, "vehicle_id": vehicle_id, "validation": validation, "checks": checks,
                "dependencies": self._dependencies(snapshot, vehicle_id),
                "directed_edge_ids": directed, "baseline": baseline, "estimated_consequence": metrics,
                "estimated_delay_s": round(delay_s, 3), "keep_route_delay_s": round(delays["KEEP_ROUTE"], 3),
                "hold_stop": (exposure or {}).get("hold_stop") if action == "HOLD_AT_STOP" else None,
                "hold_s": delay_s if action in {*KEEPS_ITS_ROUTE, "ESCALATE"} and held else 0.0,
                "is_estimate": True,
            }
        return options

    @staticmethod
    def _option_note(action: str, held: bool, exposure: dict | None) -> str:
        if held:
            return "Remain stopped until the authored obstruction is cleared"
        if action == "HOLD_AT_STOP":
            stop = (exposure or {}).get("hold_stop") or {}
            return f"Hold at {stop.get('stop_id') or 'the next stop'} until the incident clears"
        if action == "ESCALATE":
            return "Keep the route; assistance shortens the incident"
        return "Keep the route; the shuttle meets the incident's queue, or stops short of a closure until it clears"

    def _refresh_options(self, store: TrainingStore, previous: FullSnapshot, snapshot: FullSnapshot,
                         incident: Incident, pending: list[str], *, detected: bool) -> list[Event]:
        """Offers each shuttle still to be decided for its options, and withdraws those of a shuttle no
        longer to be. Options are reissued, for everyone, when who is pending changes, when they expire and
        when what they were worked out from changes."""
        live = [c for c in snapshot.candidates if c.incident_id == incident.incident_id]
        offered = list(dict.fromkeys(c.vehicle_id or incident.vehicle_id for c in live))
        # Options depend on the incident's revision, which a missed or completed response moves on
        expired = any(c.expires_at_tick < snapshot.tick or
                      c.dependency_revisions.get(incident.incident_id) != incident.aggregate_revision for c in live)
        changed = False
        if live and set(offered) == set(pending) and not expired:
            known = self._offered_dependencies(store, previous, incident, live)
            changed = any(known.get(v) != self._dependencies(snapshot, v) for v in offered)
        if set(offered) == set(pending) and not expired and not changed:
            return []
        added = [v for v in pending if v not in offered]
        incident.aggregate_revision += 1
        snapshot.candidates = [c for c in snapshot.candidates if c.incident_id != incident.incident_id]
        incident.candidate_ids = []
        if not pending:
            return [self._event(snapshot, incident, "OPTIONS_WITHDRAWN", {
                "vehicle_ids": offered, "reason": "No shuttle is still to be decided for"})]
        incident.state = "OPTIONS_READY"
        options = self._options(snapshot, incident, pending)
        first = self._first_event(store, previous, incident) if not detected else None
        data = {
            "fixture_id": self.config["fixture_id"], "map_marker": self.config.get("map_marker"),
            "incident_type": self.config.get("kind"), "options": options, "vehicle_ids": pending,
            "added_vehicle_ids": added,
            "detected_at_utc": (first or {}).get("detected_at_utc") or datetime.now(UTC).isoformat(),
            "reason": ("Options for the shuttles that will reach the incident" if not live
                       else "Refresh options after dependency changes" if changed or expired
                       else "Options for the shuttles still to be decided for"),
            # A shuttle newly offered options is worth an alert, unless the detection already sounded one
            "alert": bool(added) and not detected,
            "operational_impact": self._impact(self._guided()),
        }
        return [self._event(snapshot, incident, "OPTIONS_READY", data)]

    def _first_event(self, store: TrainingStore, snapshot: FullSnapshot, incident: Incident) -> dict | None:
        """The data of the incident's first event (its detection, or the options of one from before), or None."""
        events = store.entity_events(snapshot.session_id, incident.incident_id, snapshot.event_sequence)
        first = next((e for e in events if e.event_type in {"INCIDENT_DETECTED", "OPTIONS_READY"}), None)
        return first.data if first is not None else None

    # ----- Publication -----

    def publish(self, store: TrainingStore, proposed: FullSnapshot, *, owned: bool = False) -> FullSnapshot:
        """Actor hook: hold only the affected vehicle; leave peer movement/energy intact.

        Call at each presentation boundary instead of ``store.commit_snapshot``.
        A concurrent decision makes the publication stale and must be restaged.
        owned: as for stage.
        """
        previous = store.snapshot(proposed.session_id)
        snapshot, events = self.stage(store, proposed, owned=owned)
        return store.commit_snapshot(snapshot, events, expected_snapshot_id=previous.snapshot_id)

    def stage(self, store: TrainingStore, proposed: FullSnapshot, *,
              owned: bool = False) -> tuple[FullSnapshot, tuple[Event, ...]]:
        """This incident's effect on a publication, uncommitted.

        A boundary commits once, so coexisting incidents chain their stages into one commit.
        A caller that owns proposed and no longer uses it (owned) has it changed in place rather
        than copied; otherwise it is left as it was.
        """
        previous = store.snapshot(proposed.session_id)
        snapshot = proposed if owned else proposed.model_copy(deep=True)
        if not self.owns(snapshot):
            return snapshot, ()
        if snapshot.session.status != "RUNNING" or previous.session.status != "RUNNING":
            raise TrainingStoreError("INVALID_TRANSITION", "Incident publications require a running session")
        if abs(snapshot.simulation_time_s - snapshot.tick * self.config["step_s"]) > 1e-6:
            raise TrainingStoreError("INVALID_PAYLOAD", "Incident fixture requires the 50 ms clock")
        if proposed.snapshot_id != previous.snapshot_id:
            raise TrainingStoreError("STALE_REVISION", "Actor must stage from the latest committed checkpoint")
        events = []
        incident = next((i for i in snapshot.incidents if i.incident_id == self.config["incident_id"]), None)
        detected = False
        if incident is None:
            if snapshot.tick < self.config["trigger_tick"]:
                return snapshot, ()
            found = self._detect(snapshot)
            if found is None:
                # Deferred until its shuttle is eligible, so a dwell or a different route cannot stall publications
                return snapshot, ()
            incident, event = found
            events.append(event)
            detected = True
        self._upgrade_legacy(incident)
        if incident.state in SETTLED:
            events.extend(self._sync_exposure(snapshot, incident))
            return snapshot, tuple(events)
        if self._guided():
            # A branch cannot regain unrelated recorded-camera continuity on a later publication.
            known = self.config["incident_id"] in {i.incident_id for i in previous.incidents}
            snapshot.source = self._source(previous if known else snapshot)
            for item in snapshot.vehicles:
                item.provenance = snapshot.source.model_copy(deep=True)
        if not detected:
            events.extend(self._sync_exposure(snapshot, incident))
        events.extend(self._follow_responses(store, previous, snapshot, incident))
        events.extend(self._missed(previous, snapshot, incident))
        if snapshot.tick >= self._clears_at(snapshot, incident):
            events.extend(self._clear(store, previous, snapshot, incident))
            return snapshot, tuple(events)
        self._hold(previous, snapshot, incident)
        pending = self._pending(snapshot, incident)
        events.extend(self._refresh_options(store, previous, snapshot, incident, pending, detected=detected))
        state = self._summary_state(incident, pending)
        if state != incident.state:
            incident.state = state
            incident.aggregate_revision += 1
        return snapshot, tuple(events)

    def _detect(self, snapshot: FullSnapshot):
        """The incident detected at this publication and its INCIDENT_DETECTED event, or None while its
        shuttle (a closure that must be decided for names one) is not yet where it can be stopped."""
        guided = self._guided()
        vehicle_id = self.config.get("vehicle_id")
        vehicle = next((v for v in snapshot.vehicles if v.vehicle_id == vehicle_id), None) if vehicle_id else None
        holds = self._holds_vehicle()
        if guided:
            if vehicle is None or vehicle.route_id != self.config.get("route_id"):
                return None
            if not (vehicle.service_state == "IN_SERVICE" or (not holds and vehicle.service_state in ON_ITS_ROUTE)):
                return None
        snapshot.source = self._source(snapshot)
        if guided:
            for item in snapshot.vehicles:
                item.provenance = snapshot.source.model_copy(deep=True)
        clears = snapshot.tick + self.config["clear_after_ticks"]
        affected = self.config.get("affected_edge_ids", self.routes._fixture.get("closed_directed_edge_ids"))
        if guided and self.config.get("block_current_route_entrance", False):
            affected = [self.routes.for_route(snapshot, vehicle.route_id).first_avoidable_edge(snapshot, vehicle.route_id)]
        # Disruptions are hard road closures. An accident's congestion is applied by the actor and must not
        # make its road impassable.
        if guided or self.config.get("kind") == "ROAD_BLOCK":
            snapshot.disruptions.append(Disruption(
                disruption_id=self.config["disruption_id"], aggregate_revision=1, affected_edge_ids=affected,
                active_from_tick=snapshot.tick, active_until_tick=clears,
                reason=("Authored road obstruction; keep the route, request simulated assistance, hold at a stop "
                        "or take a validated road detour" if guided
                        else "Synthetic road block: network impact assessment"),
                provenance=snapshot.source,
            ))
        incident = self._new_incident(snapshot, revision=1, state="DETECTED", clears_at_tick=clears,
                                      vehicle_id=vehicle.vehicle_id if vehicle is not None else None)
        snapshot.incidents.append(incident)
        held = guided and holds
        if held:
            vehicle.service_state = "AWAITING_GUIDANCE"
            vehicle.aggregate_revision += 1
        event = self._event(snapshot, incident, "INCIDENT_DETECTED", {
            "fixture_id": self.config["fixture_id"],
            "incident_type": self.config.get("kind"),
            "map_marker": self.config.get("map_marker"),
            "incident_edge_ids": affected,
            "impact_edge_ids": self.config.get("impact_edge_ids", []),
            "requires_guidance": guided,
            "active_until_tick": clears,
            "operational_impact": self._impact(guided),
            "reason": ("Closure on the route of the shuttle it was placed ahead of; operator review required" if guided
                       else "Network incident; each shuttle that will reach it is offered options"),
            "detected_at_utc": datetime.now(UTC).isoformat(),
            "battery_at_detection_kwh": vehicle.battery_kwh if vehicle is not None else None,
            "transitions": ["DETECTED"],
            "vehicle_transitions": ["BLOCKED", "AWAITING_GUIDANCE"] if held else [],
            # Only an event a shuttle will reach is worth an alert sound
            "alert": guided or bool(incident.exposed_vehicle_ids),
            **self._world(),
        })
        return incident, event

    def _hold(self, previous: FullSnapshot, snapshot: FullSnapshot, incident: Incident) -> None:
        """Keeps the shuttle stopped where the incident was detected (a session without background traffic)
        until the event clears, unless it was sent round it."""
        held = self._held(incident)
        if held is None or any(r.vehicle_id == held and r.action == "ROAD_DETOUR" for r in incident.responses):
            return
        vehicle = next((v for v in snapshot.vehicles if v.vehicle_id == held), None)
        before = next((v for v in previous.vehicles if v.vehicle_id == held), None)
        if vehicle is None or before is None or vehicle.service_state not in {"AWAITING_GUIDANCE", *ON_ITS_ROUTE}:
            return
        vehicle.position = before.position.model_copy(deep=True)
        vehicle.speed_mps = 0.0
        vehicle.service_state = "AWAITING_GUIDANCE"

    def _follow_responses(self, store: TrainingStore, previous: FullSnapshot, snapshot: FullSnapshot,
                          incident: Incident) -> list[Event]:
        """Ends each detour once its shuttle has driven it (or left it for a charger)."""
        events = []
        vehicles = {v.vehicle_id: v for v in snapshot.vehicles}
        for response in incident.responses:
            vehicle = vehicles.get(response.vehicle_id)
            if (response.action != "ROAD_DETOUR" or response.completed_tick is not None or vehicle is None
                    or vehicle.service_state not in {"IN_SERVICE", *LEFT_SERVICE}):
                continue
            response.completed_tick = snapshot.tick
            incident.aggregate_revision += 1
            # A shuttle sent to a charger mid-detour never comes back to it, so the response ends there
            reason = ("Actor completed the approved detour" if vehicle.service_state == "IN_SERVICE"
                      else "The shuttle left the approved detour for a charger")
            events.append(self._event(snapshot, incident, "INCIDENT_RESPONSE_COMPLETED", {
                "vehicle_id": response.vehicle_id, "action": response.action, "reason": reason, "camera": None,
                "service_state": vehicle.service_state, "observed_battery_kwh": vehicle.battery_kwh}))
        return events

    def _missed(self, previous: FullSnapshot, snapshot: FullSnapshot, incident: Incident) -> list[Event]:
        """A shuttle offered options that drives into the incident's footprint undecided has made its decision,
        and scores as an unresolved incident does. It may still be decided for, as a late decision."""
        before = next((i for i in previous.incidents if i.incident_id == incident.incident_id), None)
        if before is None:
            return []
        offered = {c.vehicle_id or incident.vehicle_id for c in previous.candidates if c.incident_id == incident.incident_id}
        was = {e.vehicle_id: e.distance_m for e in before.exposures}
        responded = {r.vehicle_id for r in incident.responses}
        events = []
        for exposure in incident.exposures:
            vehicle_id = exposure.vehicle_id
            if (vehicle_id in responded or vehicle_id not in offered or exposure.distance_m > 0
                    or was.get(vehicle_id, 0.0) <= 0):
                continue
            events.append(self._no_decision(snapshot, incident, vehicle_id,
                                            "The shuttle reached the incident without a decision"))
        return events

    def _no_decision(self, snapshot: FullSnapshot, incident: Incident, vehicle_id: str, reason: str) -> Event:
        incident.responses.append(IncidentResponse(vehicle_id=vehicle_id, action="NO_DECISION",
                                                   decided_tick=snapshot.tick, before_arrival=False))
        incident.aggregate_revision += 1
        return self._event(snapshot, incident, "INCIDENT_RESPONSE_MISSED", {
            "vehicle_id": vehicle_id, "reason": reason, "assessment": self._unanswered(vehicle_id)})

    def _unanswered(self, vehicle_id: str | None) -> dict:
        """The assessment of a shuttle nobody decided for: zero on every criterion."""
        return {"rubric_revision": self.config["rubric_revision"], "provisional": True, "uncalibrated": True,
                "scope": "INCIDENT_ONLY", "vehicle_id": vehicle_id,
                "criteria": dict.fromkeys(self.config["rubric"], 0),
                "maximum_points": self.config["rubric"], "total": 0, "attempts": 0,
                "assumptions": self.config["assumptions"]}

    def _clear(self, store: TrainingStore, previous: FullSnapshot, snapshot: FullSnapshot,
               incident: Incident) -> list[Event]:
        """The event has cleared: the held shuttle is released (with no decision, if it had none), holds at
        stops end, options are withdrawn, and the incident settles once every detour round it is driven."""
        events = []
        vehicles = {v.vehicle_id: v for v in snapshot.vehicles}
        held = self._held(incident)
        decided = {r.vehicle_id for r in incident.responses if r.action != "NO_DECISION"}
        released = None
        if held is not None and held in vehicles and vehicles[held].service_state == "AWAITING_GUIDANCE":
            if held not in decided and not any(r.vehicle_id == held for r in incident.responses):
                events.append(self._no_decision(snapshot, incident, held, "The shuttle was still held, undecided, when the obstruction cleared"))
            released = vehicles[held]
            released.service_state = "IN_SERVICE"
            released.aggregate_revision += 1
        for response in incident.responses:
            if response.action != "ROAD_DETOUR" and response.completed_tick is None:
                response.completed_tick = snapshot.tick
        if incident.candidate_ids:
            snapshot.candidates = [c for c in snapshot.candidates if c.incident_id != incident.incident_id]
            incident.candidate_ids = []
        if any(r.action == "ROAD_DETOUR" and r.completed_tick is None for r in incident.responses):
            if incident.state != "EXECUTING":
                incident.state = "EXECUTING"
                incident.aggregate_revision += 1
            return events
        missed = [r.vehicle_id for r in incident.responses if r.action == "NO_DECISION"]
        incident.state = "UNRESOLVED" if missed else "RESOLVED"
        incident.aggregate_revision += 1
        data = {"reason": ("Authored obstruction cleared" if self._guided() else "Synthetic network incident duration elapsed"),
                "camera": None, "operational_delay_s": (snapshot.tick - incident.detected_tick) * self.config["step_s"],
                "responses": [r.model_dump(mode="json") for r in incident.responses],
                "missed_vehicle_ids": missed}
        if released is not None:
            data["service_state"] = released.service_state
            data["observed_battery_kwh"] = released.battery_kwh
            first = self._first_event(store, previous, incident)
            if first and first.get("battery_at_detection_kwh") is not None:
                data["observed_battery_change_kwh"] = released.battery_kwh - first["battery_at_detection_kwh"]
        if missed:
            # An incident left unanswered scores zero, as each of its undecided shuttles does
            data["assessment"] = {**self._unanswered(missed[0]),
                                  "attempts": len(store.incident_attempts(snapshot.session_id, incident.incident_id))}
        events.append(self._event(snapshot, incident, "INCIDENT_" + incident.state, data))
        return events

    # ----- Decisions -----

    def _evidence(self, store: TrainingStore, snapshot: FullSnapshot, incident: Incident) -> Event:
        events = store.entity_events(snapshot.session_id, incident.incident_id, snapshot.event_sequence)
        evidence = next((e for e in reversed(events) if e.event_type == "OPTIONS_READY" and "options" in e.data), None)
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
        candidate = next(c for c in snapshot.candidates if c.candidate_id == candidate_id)
        vehicle_id = candidate.vehicle_id or incident.vehicle_id
        if any(r.vehicle_id == vehicle_id and r.action != "NO_DECISION" for r in incident.responses):
            raise TrainingStoreError("INVALID_TRANSITION", "This shuttle has already been decided for")
        evidence = self._evidence(store, snapshot, incident)
        option = evidence.data["options"].get(candidate_id)
        if option is None or option["dependencies"] != self._dependencies(snapshot, vehicle_id):
            raise TrainingStoreError("STALE_REVISION", "Obstruction or route dependencies changed")
        clears = self._clears_at(snapshot, incident)
        closure = self._disruption(snapshot)
        if snapshot.tick >= clears or (closure is not None and snapshot.tick < closure.active_from_tick):
            raise TrainingStoreError("STALE_REVISION", "Obstruction is no longer active")
        vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == vehicle_id)
        held = vehicle_id == self._held(incident)
        if held:
            if vehicle.service_state != "AWAITING_GUIDANCE" or vehicle.speed_mps != 0.0:
                raise TrainingStoreError("INVALID_TRANSITION", "Affected shuttle must be safely stopped")
        elif vehicle.service_state not in DECIDABLE:
            # Not held, it may be decided for before it gets there, so long as it is still on its route
            raise TrainingStoreError("INVALID_TRANSITION", "Affected shuttle is no longer on its route")
        if candidate.action != option["action"] or candidate.validation != option["validation"]:
            raise TrainingStoreError("STALE_REVISION", "Option evidence changed")
        result = snapshot.model_copy(deep=True)
        result.source = self._source(snapshot)
        for item in result.vehicles:
            item.provenance = result.source.model_copy(deep=True)
        target = next(i for i in result.incidents if i.incident_id == incident.incident_id)
        shuttle = next(v for v in result.vehicles if v.vehicle_id == vehicle_id)
        action = "KEEP_ROUTE" if option["action"] == "WAIT" else option["action"]
        exposure = self._exposure(vehicle_id)
        before_arrival = held or exposure is None or float(exposure["distance_m"]) > 0
        # The operator's own detour from the route editor, in place of the planned one.
        chosen = command.payload.get("directed_edge_ids")
        if chosen is not None and (action != "ROAD_DETOUR" or not isinstance(chosen, list) or not 0 < len(chosen) <= 1024
                                   or not all(isinstance(edge, str) for edge in chosen)):
            raise TrainingStoreError("INVALID_PAYLOAD", "Only a road detour takes a list of directed road segments")
        # Recompute the consequences at decision time, not at option-generation time.
        delays = self._delays(snapshot, incident, vehicle_id)
        baseline = option["baseline"]
        checks = []
        if action == "ROAD_DETOUR":
            planning = self._avoiding(snapshot, vehicle_id)
            refreshed = (self._route_service(snapshot, vehicle_id).chosen_continuation(planning, vehicle_id, chosen)
                         if chosen else self._road_preview(snapshot, vehicle_id))
            if not refreshed.approvable or refreshed.directed_edge_ids != (chosen or option["directed_edge_ids"]):
                raise TrainingStoreError("NO_FEASIBLE_ROUTE", "Road detour no longer passes validation")
            consequence = refreshed.edited.model_dump(mode="json")
            delay_s = max(0.0, consequence["duration_s"] - baseline["duration_s"]) if baseline else 0.0
            route = next(r for r in snapshot.routes if r.route_id == shuttle.route_id)
            detour = RouteRevision(route_id=detour_route_id(incident.incident_id, vehicle_id), revision=1,
                                   scene_revision=snapshot.scene_revision, directed_edge_ids=refreshed.directed_edge_ids,
                                   edge_paths_m=refreshed.edge_paths_m(),
                                   required_stop_ids=route.required_stop_ids, stops_m=route.stops_m,
                                   distance_m=refreshed.edited.distance_m,
                                   estimated_duration_s=refreshed.edited.duration_s)
            result.routes = [r for r in result.routes if r.route_id != detour.route_id]
            result.routes.append(detour)
            shuttle.route_id, shuttle.route_revision = detour.route_id, detour.revision
            shuttle.service_state = "DETOURING"
            transitions = ["ACTION_COMMITTED", "EXECUTING"]
            checks = [c.model_dump(mode="json") for c in refreshed.checks]
        else:
            if action == "ESCALATE":
                until = min(clears, snapshot.tick + self.config["escalation_after_ticks"])
                target.clears_at_tick = until
                live = next((d for d in result.disruptions if d.disruption_id == self.config.get("disruption_id")), None)
                if live is not None and live.active_until_tick is not None:
                    live.active_until_tick = min(live.active_until_tick, until)
                    live.aggregate_revision += 1
                delays = self._delays(result, target, vehicle_id)
            delay_s = delays.get("ESCALATE" if action == "ESCALATE" else action, delays["KEEP_ROUTE"])
            if self._detour_capable(snapshot, vehicle_id) and closure is not None and action != "HOLD_AT_STOP":
                continuation_state = result.model_copy(deep=True)
                continuation_state.tick = self._clears_at(result, target)
                route = next(r for r in result.routes if r.route_id == shuttle.route_id)
                continuation = self._route_service(continuation_state, vehicle_id).preview(continuation_state, RouteEditRequest(
                    route_id=route.route_id, expected_route_revision=route.revision, draft_revision=1,
                    directed_edge_ids=route.directed_edge_ids,
                ), retain=False)
                if not continuation.approvable:
                    raise TrainingStoreError("NO_FEASIBLE_ROUTE", "Safe continuation after the hold is no longer feasible")
                checks = [c.model_dump(mode="json") for c in continuation.checks]
            consequence = dict(baseline) if baseline else None
            if consequence:
                consequence["duration_s"] += delay_s
                consequence["energy_kwh"] += self.routes._fixture["auxiliary_kw"] * delay_s / 3600
            transitions = ["ESCALATED" if action == "ESCALATE" else "WAITING"]
        # The shuttle's decision replaces a NO_DECISION it was given for reaching the incident undecided
        target.responses = [r for r in target.responses if r.vehicle_id != vehicle_id]
        stop = ((exposure or {}).get("hold_stop") or option.get("hold_stop") or {}) if action == "HOLD_AT_STOP" else {}
        target.responses.append(IncidentResponse(vehicle_id=vehicle_id, action=action, decided_tick=snapshot.tick,
                                                 before_arrival=before_arrival, hold_stop_id=stop.get("stop_id")))
        # Everyone else's options depended on the incident's revision: they are reissued at the next publication
        result.candidates = [c for c in result.candidates if c.incident_id != incident.incident_id]
        target.candidate_ids = []
        remaining = [v for v in self._pending(result, target) if v != vehicle_id]
        target.state = self._summary_state(target, remaining)
        target.aggregate_revision += 1
        shuttle.aggregate_revision += 1
        keep_s = delays["KEEP_ROUTE"] if action != "ESCALATE" else self._delays(snapshot, incident, vehicle_id)["KEEP_ROUTE"]
        offered = [o for o in evidence.data["options"].values()
                   if o.get("vehicle_id", vehicle_id) == vehicle_id and o["validation"] == "PASS"]
        best_s = min([delay_s, *(o.get("estimated_delay_s", delay_s) for o in offered)])
        response_s = round(snapshot.simulation_time_s - incident.detected_tick * self.config["step_s"], 6)
        return result, {
            "command_id": command.command_id, "candidate_id": candidate_id, "action": action,
            "vehicle_id": vehicle_id, "reason": command.reason, "transitions": transitions,
            "hold_stop_id": stop.get("stop_id"),
            "response_time_s": response_s, "before_arrival": before_arrival,
            "timing_basis": "SERVER_SIMULATION_CLOCK_EXCLUDES_PAUSES",
            "estimated_consequence": consequence, "baseline": baseline, "is_estimate": True,
            "estimated_delay_s": round(delay_s, 3), "keep_route_delay_s": round(keep_s, 3),
            "best_delay_s": round(best_s, 3), "delay_avoided_s": round(keep_s - delay_s, 3),
            "estimated_additional_service_delay_s": round(delay_s + (response_s if held else 0.0), 6),
            "estimated_additional_energy_kwh": round(self.routes._fixture["auxiliary_kw"] *
                                                     (delay_s + (response_s if held else 0.0)) / 3600, 6),
            "rule_outcomes": checks,
            "energy_model_revision": snapshot.scenario.energy_model_revision,
            "consequence_model": "Queue length at the speed in the queue, or the wait for the event to clear; "
                                 "detours from the route editor's metrics",
        }

    def _score(self, decision: dict, attempts: int) -> dict:
        limits = self.config["rubric"]
        elapsed = decision["response_time_s"]
        time_fraction = max(0.0, min(1.0, (self.config["zero_time_credit_s"] - elapsed) /
                                    (self.config["zero_time_credit_s"] - self.config["full_time_credit_s"])))
        consequence = decision["estimated_consequence"] or {}
        coverage = consequence.get("stop_coverage")
        criteria = {
            "safety_compliance": limits["safety_compliance"],
            "response_time": round(limits["response_time"] * response_share(time_fraction, decision["before_arrival"]), 2),
            "attempts": max(0, limits["attempts"] - 2 * (attempts - 1)),
            "service_coverage": limits["service_coverage"] if coverage is None or all(coverage.values()) else 0,
            "delay_avoided": round(limits["delay_avoided"] * delay_share(
                decision["keep_route_delay_s"], decision["estimated_delay_s"], decision["best_delay_s"]), 2),
        }
        return {"rubric_revision": self.config["rubric_revision"], "provisional": True,
                "scope": "INCIDENT_ONLY", "uncalibrated": True, "vehicle_id": decision["vehicle_id"],
                "criteria": criteria, "maximum_points": limits, "total": round(sum(criteria.values()), 2),
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
        incident = self._incident(snapshot, accepted.target_id)
        attempts = store.incident_attempts(snapshot.session_id, accepted.target_id)
        record = next(a for a in attempts if a["command_id"] == accepted.command_id)
        first = self._first_event(store, snapshot, incident) or {}
        detected_at = first.get("detected_at_utc")
        data["wall_response_time_s"] = (max(0.0, (datetime.fromisoformat(record["accepted_at_utc"]) -
                                                  datetime.fromisoformat(detected_at)).total_seconds())
                                        if detected_at else None)
        # Attempts count per shuttle: its options' ids name it (option_vehicle). An attempt at an option that
        # names no shuttle of this session (one never offered, or one from before per-shuttle options) counts
        # for every shuttle.
        fleet = {v.vehicle_id for v in snapshot.vehicles}

        def counts(attempt) -> bool:
            chosen = str((attempt["command"].get("payload") or {}).get("candidate_id", ""))
            named = option_vehicle(accepted.target_id, chosen)
            return named == data["vehicle_id"] or named not in fleet
        mine = [a for a in attempts if counts(a)]
        attempt_count = next((index + 1 for index, attempt in enumerate(mine) if attempt["command_id"] == accepted.command_id),
                             len(mine) or 1)
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
        """The incident's record: each shuttle's decision and assessment (the latest for each), the latest
        assessment of all, and the outcome once it has settled."""
        snapshot = store.snapshot(session_id)
        incident = self._incident(snapshot, incident_id)
        events = store.entity_events(session_id, incident_id, snapshot.event_sequence)
        decisions = [e.data for e in events if e.event_type == "INCIDENT_DECISION_RECORDED"]
        assessed = [e.data["assessment"] for e in events if "assessment" in e.data]
        outcomes = [e.data for e in events if e.event_type in {"INCIDENT_RESOLVED", "INCIDENT_UNRESOLVED"}]
        by_vehicle = {}
        for e in events:
            if e.event_type in {"INCIDENT_DECISION_RECORDED", "INCIDENT_RESPONSE_MISSED"} and "assessment" in e.data:
                by_vehicle[e.data.get("vehicle_id") or incident.vehicle_id] = e.data["assessment"]
        return {"incident_id": incident_id, "state": incident.state, "provisional": True,
                "rubric_revision": self.config["rubric_revision"],
                "assessment": assessed[-1] if assessed else None,
                "assessments": by_vehicle,
                "responses": [r.model_dump(mode="json") for r in incident.responses],
                "outcome": outcomes[-1] if outcomes else None,
                "reference_acceptable_actions": self.config["reference_acceptable_actions"],
                "attempts": store.incident_attempts(session_id, incident_id),
                "decisions": decisions, "events": [e.model_dump(mode="json") for e in events]}


@lru_cache
def get_incident_service() -> IncidentService:
    return IncidentService(get_route_edit_service())
