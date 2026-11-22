"""Canonical M2 wire models and semantic validation for the fleet contract."""

from __future__ import annotations

from typing import Annotated, Any, Literal, Union

from pydantic import BaseModel, ConfigDict, Field, FiniteFloat, StringConstraints, field_validator, model_validator
from .spatial import SpatialReference, SpatialVehicle

Id = Annotated[str, StringConstraints(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9][A-Za-z0-9_.:-]*$")]
Text = Annotated[str, StringConstraints(min_length=1, max_length=512)]
Revision = Annotated[int, Field(ge=0)]
Tick = Annotated[int, Field(ge=0)]

SESSION_TRANSITIONS = {
    "DRAFT": {"READY", "FAILED"}, "READY": {"RUNNING", "ARCHIVED", "FAILED"},
    "RUNNING": {"PAUSED", "COMPLETED", "RECOVERING", "ARCHIVED", "FAILED"},
    "PAUSED": {"RUNNING", "COMPLETED", "RECOVERING", "ARCHIVED", "FAILED"},
    "RECOVERING": {"PAUSED", "FAILED"}, "COMPLETED": {"ARCHIVED"},
    "ARCHIVED": set(), "FAILED": set(),
}
VEHICLE_TRANSITIONS = {
    "STANDBY": {"IN_SERVICE", "RETURNING_TO_DEPOT", "OUT_OF_SERVICE"},
    "IN_SERVICE": {"DWELLING", "BLOCKED", "DETOURING", "RETURNING_TO_DEPOT", "OUT_OF_SERVICE"},
    "DWELLING": {"IN_SERVICE", "BLOCKED", "RETURNING_TO_DEPOT", "OUT_OF_SERVICE"},
    "BLOCKED": {"AWAITING_GUIDANCE", "IN_SERVICE", "OUT_OF_SERVICE"},
    "AWAITING_GUIDANCE": {"DETOURING", "IN_SERVICE", "RETURNING_TO_DEPOT", "OUT_OF_SERVICE"},
    "DETOURING": {"IN_SERVICE", "DWELLING", "BLOCKED", "RETURNING_TO_DEPOT"},
    "RETURNING_TO_DEPOT": {"STANDBY", "QUEUED_FOR_CHARGE", "OUT_OF_SERVICE"},
    "QUEUED_FOR_CHARGE": {"CHARGING", "STANDBY", "OUT_OF_SERVICE"},
    "CHARGING": {"RETURNING_TO_DEPOT", "STANDBY", "OUT_OF_SERVICE"}, "OUT_OF_SERVICE": {"STANDBY"},
}
# An incident's state sums up its shuttles' decisions (Incident.responses): OPTIONS_READY while an exposed
# shuttle has still to be decided for, and back to it when another shuttle becomes exposed; then EXECUTING
# while a shuttle drives a detour round it, ESCALATED once assistance is on its way, WAITING while every
# decided shuttle keeps its route or holds, and DETECTED when nobody has had to decide. It settles when the
# event clears: UNRESOLVED when a shuttle reached it with no decision, else RESOLVED.
INCIDENT_TRANSITIONS = {
    "DETECTED": {"AWAITING_REVIEW", "OPTIONS_READY", "RESOLVED"},
    "AWAITING_REVIEW": {"OPTIONS_READY", "ESCALATED", "UNRESOLVED"},
    "OPTIONS_READY": {"ACTION_COMMITTED", "WAITING", "ESCALATED", "EXECUTING", "DETECTED", "RESOLVED", "UNRESOLVED"},
    "ACTION_COMMITTED": {"EXECUTING", "AWAITING_REVIEW"},
    "EXECUTING": {"RESOLVED", "UNRESOLVED", "AWAITING_REVIEW", "OPTIONS_READY", "ESCALATED", "WAITING"},
    "WAITING": {"AWAITING_REVIEW", "OPTIONS_READY", "EXECUTING", "ESCALATED", "RESOLVED", "UNRESOLVED"},
    "ESCALATED": {"OPTIONS_READY", "EXECUTING", "RESOLVED", "UNRESOLVED"}, "RESOLVED": set(), "UNRESOLVED": set(),
}
# What an operator can tell one exposed shuttle to do about an incident. WAIT is KEEP_ROUTE's name in
# journals from before per-shuttle decisions; RETURN_TO_DEPOT and CHARGE are not offered for incidents.
INCIDENT_ACTIONS = ("KEEP_ROUTE", "ROAD_DETOUR", "ESCALATE", "HOLD_AT_STOP")


def transition_allowed(machine: Literal["session", "vehicle", "incident"], before: str, after: str) -> bool:
    table = {"session": SESSION_TRANSITIONS, "vehicle": VEHICLE_TRANSITIONS, "incident": INCIDENT_TRANSITIONS}[machine]
    return after in table.get(before, set())


class WireModel(BaseModel):
    model_config = ConfigDict(extra="allow", strict=True)


class Provenance(WireModel):
    kind: Literal["TRAINING_SIMULATION", "HISTORICAL_SIMULATION", "RECORDED_SENSOR"]
    recording_id: Id | None = None
    sequence_id: Id | None = None
    source_frame: Tick | None = None
    source_timestamp: str | None = None
    branch_tick: Tick | None = None
    authored: bool = False

    @model_validator(mode="after")
    def recorded_has_source(self) -> "Provenance":
        if self.kind == "RECORDED_SENSOR" and (not self.recording_id or not self.sequence_id or self.source_frame is None or not self.source_timestamp):
            raise ValueError("RECORDED_SENSOR requires recording_id, sequence_id, source_frame and source_timestamp")
        if self.kind != "RECORDED_SENSOR" and self.source_frame is not None:
            raise ValueError("source_frame belongs only to recorded sensor data")
        return self


class ScenarioRevision(WireModel):
    scenario_id: Id
    revision_id: Id
    scene_revision: Id
    transform_revision: Id
    rules_revision: Id
    vehicle_model_revision: Id
    energy_model_revision: Id
    rubric_revision: Id
    seed: int
    provenance: Provenance
    spatial: SpatialReference | None = None
    assessment_duration_s: int | None = Field(default=None, gt=0,
        description="Simulation seconds available in a scored attempt, pinned at creation.")


class TrainingSession(WireModel):
    session_id: Id
    scenario_revision_id: Id
    status: Literal["DRAFT", "READY", "RUNNING", "PAUSED", "RECOVERING", "COMPLETED", "ARCHIVED", "FAILED"]
    session_epoch: Revision
    aggregate_revision: Revision
    authority_id: Id


class Position(WireModel):
    x_m: float
    y_m: float
    z_m: float


class FleetVehicle(WireModel):
    vehicle_id: Id
    aggregate_revision: Revision
    service_state: Literal["STANDBY", "IN_SERVICE", "DWELLING", "BLOCKED", "AWAITING_GUIDANCE", "DETOURING", "RETURNING_TO_DEPOT", "QUEUED_FOR_CHARGE", "CHARGING", "OUT_OF_SERVICE"]
    route_id: Id | None
    route_revision: Revision | None
    position: Position
    heading_deg: float
    speed_mps: Annotated[float, Field(ge=0)]
    battery_kwh: Annotated[float, Field(ge=0)]
    battery_capacity_kwh: Annotated[float, Field(gt=0)]
    provenance: Provenance
    spatial: SpatialVehicle | None = None

    @model_validator(mode="after")
    def valid_battery_route(self) -> "FleetVehicle":
        if self.battery_kwh > self.battery_capacity_kwh:
            raise ValueError("battery_kwh exceeds capacity")
        if (self.route_id is None) != (self.route_revision is None):
            raise ValueError("route_id and route_revision must be supplied together")
        return self


class RouteStop(WireModel):
    stop_id: Id
    position: Position


class RouteRevision(WireModel):
    route_id: Id
    revision: Revision
    scene_revision: Id
    directed_edge_ids: list[Id]
    required_stop_ids: list[Id]
    distance_m: Annotated[float, Field(ge=0)]
    estimated_duration_s: Annotated[float, Field(ge=0)]
    # Drawable geometry: entry i is the polyline of directed_edge_ids[i] in local metres.
    # Empty when the route's source has no geometry; clients then cannot draw it.
    edge_paths_m: Annotated[list[Annotated[list[Position], Field(min_length=2, max_length=1024)]],
                            Field(max_length=1024)] = []
    # Where each required stop is, in local metres. Empty when the source has no stop positions.
    stops_m: Annotated[list[RouteStop], Field(max_length=64)] = []

    @model_validator(mode="after")
    def edge_paths_match_edges(self) -> "RouteRevision":
        if self.edge_paths_m and len(self.edge_paths_m) != len(self.directed_edge_ids):
            raise ValueError("edge_paths_m must have one path per directed edge")
        if any(stop.stop_id not in self.required_stop_ids for stop in self.stops_m):
            raise ValueError("stops_m may only place required stops")
        return self


class Disruption(WireModel):
    disruption_id: Id
    aggregate_revision: Revision
    affected_edge_ids: list[Id]
    active_from_tick: Tick
    active_until_tick: Tick | None = None
    reason: Text
    provenance: Provenance


class Candidate(WireModel):
    candidate_id: Id
    incident_id: Id
    revision: Revision
    action: Literal["KEEP_ROUTE", "ESCALATE", "ROAD_DETOUR", "HOLD_AT_STOP", "WAIT", "RETURN_TO_DEPOT", "CHARGE"]
    validation: Literal["PASS", "WARN", "FAIL", "UNKNOWN"]
    expires_at_tick: Tick
    dependency_revisions: dict[str, Revision]
    assumptions: list[str]
    # The shuttle this option is for: an incident offers options per exposed shuttle. None in 2.x
    # snapshots from before per-shuttle options, where it is the incident's own vehicle_id.
    vehicle_id: Id | None = None


class IncidentLocation(WireModel):
    """Where a world event is: one operating-network road (edge_id) or one junction (node_id)."""
    edge_id: Id | None = None
    node_id: Id | None = None
    road_name: Text | None = None

    @model_validator(mode="after")
    def one_place(self) -> "IncidentLocation":
        if (self.edge_id is None) == (self.node_id is None):
            raise ValueError("an incident location is exactly one edge or one junction")
        return self


class IncidentExposure(WireModel):
    """A shuttle that will reach an incident while it lasts, how far away and about when.

    Its remaining route crosses the incident's footprint (the scene and the queue behind it). Refreshed
    as shuttles move."""
    vehicle_id: Id
    distance_m: Annotated[FiniteFloat, Field(ge=0)]
    eta_s: Annotated[FiniteFloat, Field(ge=0)]
    is_estimate: Literal[True] = True
    # About how long keeping its route would hold it up: in the queue, or waiting for a closure or a stuck
    # light to clear. None in 2.x snapshots from before decisions were scored by the delay they avoid.
    keep_route_delay_s: Annotated[FiniteFloat, Field(ge=0)] | None = None


class IncidentResponse(WireModel):
    """What the operator decided for one exposed shuttle, or that it reached the incident undecided.

    NO_DECISION is recorded when a shuttle drives into the incident's footprint, or is still held at it
    when it clears, without a decision; it is scored as an unresolved incident is."""
    vehicle_id: Id
    action: Literal["KEEP_ROUTE", "ROAD_DETOUR", "ESCALATE", "HOLD_AT_STOP", "WAIT", "NO_DECISION"]
    decided_tick: Tick
    # Whether it was decided before the shuttle entered the footprint, which full response credit needs
    before_arrival: bool
    # When what it set going ended: a detour's loop driven, a hold at a stop released. None while it lasts.
    completed_tick: Tick | None = None
    # The stop a HOLD_AT_STOP holds its shuttle at; None for any other action
    hold_stop_id: Id | None = None


class Incident(WireModel):
    incident_id: Id
    aggregate_revision: Revision
    # The shuttle held for a decision, or the one the incident was placed ahead of; None for a world
    # event that belongs to no shuttle. exposed_vehicle_ids says which shuttles it affects.
    vehicle_id: Id | None = None
    state: Literal["DETECTED", "AWAITING_REVIEW", "OPTIONS_READY", "ACTION_COMMITTED", "EXECUTING", "WAITING", "ESCALATED", "RESOLVED", "UNRESOLVED"]
    detected_tick: Tick
    candidate_ids: list[Id]
    provenance: Provenance
    # The world event behind the incident. None in 2.x snapshots from before world events.
    event_type: Literal["BREAKDOWN", "COLLISION", "SIGNAL_FAULT", "ROAD_CLOSURE", "PEDESTRIAN_ON_ROAD"] | None = None
    location: IncidentLocation | None = None
    # Shuttles that will reach the incident while it lasts, nearest first, and each one's distance and
    # ETA. Live data like a vehicle's position: it changes without a new aggregate_revision.
    exposed_vehicle_ids: list[Id] = Field(default_factory=list)
    exposures: list[IncidentExposure] = Field(default_factory=list)
    # One per shuttle decided for (or that reached it undecided), in the order they were recorded. Empty in
    # 2.x snapshots from before per-shuttle decisions.
    responses: list[IncidentResponse] = Field(default_factory=list)
    # The tick the world event is expected to clear; an ESCALATE brings it forward. None in older 2.x snapshots.
    clears_at_tick: Tick | None = None

    @model_validator(mode="after")
    def exposures_match(self) -> "Incident":
        if [e.vehicle_id for e in self.exposures] != self.exposed_vehicle_ids:
            raise ValueError("incident exposures must list exactly its exposed vehicles, in order")
        if len(self.exposed_vehicle_ids) != len(set(self.exposed_vehicle_ids)):
            raise ValueError("duplicate exposed vehicle")
        decided = [r.vehicle_id for r in self.responses]
        if len(decided) != len(set(decided)):
            raise ValueError("an incident has at most one response per vehicle")
        return self


class Charger(WireModel):
    charger_id: Id
    aggregate_revision: Revision
    port_count: Annotated[int, Field(ge=1)]
    power_kw: Annotated[float, Field(gt=0)]
    occupied_vehicle_ids: list[Id]
    position: Position | None = None

class Depot(WireModel):
    depot_id: Id
    position: Position
    charger_ids: list[Id]

class EnergyForecast(WireModel):
    vehicle_id: Id
    energy_model_revision: Id
    reserve_kwh: Annotated[FiniteFloat, Field(ge=0)]
    reserve_margin_kwh: FiniteFloat
    loops_remaining: Annotated[int, Field(ge=0)] | None
    next_depot_id: Id | None = None
    next_depot_eta_s: Annotated[FiniteFloat, Field(ge=0)] | None = None
    reaches_depot_above_reserve: bool | None = None
    is_estimate: Literal[True] = True

    @model_validator(mode="after")
    def depot_fields_together(self) -> "EnergyForecast":
        if self.next_depot_id is None and (self.next_depot_eta_s is not None or self.reaches_depot_above_reserve is not None):
            raise ValueError("next_depot_eta_s and reaches_depot_above_reserve need next_depot_id")
        if self.next_depot_id is not None and self.next_depot_eta_s is None:
            raise ValueError("next_depot_id needs next_depot_eta_s")
        return self


class ChargeOption(WireModel):
    option_id: Id
    vehicle_id: Id
    revision: Revision
    action: Literal["CHARGE_NOW", "CONTINUE", "RETURN_TO_DEPOT"]
    charger_id: Id | None = None
    validation: Literal["PASS", "WARN", "FAIL", "UNKNOWN"]
    expires_at_tick: Tick
    offered_at_tick: Tick | None = None
    dependency_revisions: dict[str, Revision]
    out_of_service_s: Annotated[FiniteFloat, Field(ge=0)]
    battery_after_kwh: Annotated[FiniteFloat, Field(ge=0)]
    loops_after: Annotated[int, Field(ge=0)] | None
    min_in_service_vehicles: Annotated[int, Field(ge=0)] | None = None
    assumptions: list[str]
    is_estimate: Literal[True] = True

    @model_validator(mode="after")
    def action_fields_match(self) -> "ChargeOption":
        if self.action == "CHARGE_NOW" and self.charger_id is None:
            raise ValueError("CHARGE_NOW needs a charger_id")
        if self.action == "CONTINUE" and (self.charger_id is not None or self.out_of_service_s != 0):
            raise ValueError("CONTINUE cannot use a charger or take the shuttle out of service")
        if self.offered_at_tick is not None and self.offered_at_tick > self.expires_at_tick:
            raise ValueError("charge option cannot be offered after it expires")
        return self


class ChargeBooking(WireModel):
    """One shuttle's hold on a charger port over a window of simulation ticks.

    SCHEDULED holds a future slot. ACTIVE means the port is taken, from dispatch through charging
    until the shuttle has cleared the bay. COMPLETED, CANCELLED and MISSED are final. The window
    is an estimate: an overrunning ACTIVE booking has its end_tick extended by the backend.
    """
    booking_id: Id
    revision: Revision
    vehicle_id: Id
    charger_id: Id
    status: Literal["SCHEDULED", "ACTIVE", "COMPLETED", "CANCELLED", "MISSED"]
    origin: Literal["OPERATOR", "CHARGE_NOW", "SAFETY_RETURN"]
    start_tick: Tick
    end_tick: Tick
    status_reason: Text | None = None
    # While ACTIVE: what the shuttle is doing now, the way it is driving, and where its riders get off.
    phase: Literal["DROPPING_PASSENGERS", "DRIVING_TO_CHARGER", "CHARGING", "LEAVING_BAY", "RETURNING_TO_ROUTE"] | None = None
    trip_path_m: list[Annotated[list[FiniteFloat], Field(min_length=2, max_length=2)]] = Field(default_factory=list)
    set_down_stop_id: Id | None = None
    is_estimate: Literal[True] = True

    @model_validator(mode="after")
    def window_is_ordered(self) -> "ChargeBooking":
        if self.end_tick <= self.start_tick:
            raise ValueError("charge booking must end after it starts")
        return self


class ChargeScheduleRequest(WireModel):
    """A slot the operator wants checked; replaces_booking_id moves an existing SCHEDULED booking."""
    vehicle_id: Id
    charger_id: Id
    start_tick: Tick
    replaces_booking_id: Id | None = None


class ChargeScheduleIssue(WireModel):
    code: Id
    message: Text
    blocking: bool


class ChargeSchedulePreview(WireModel):
    """The backend's assessment of a requested slot. It reserves nothing."""
    session_id: Id
    tick: Tick
    vehicle_id: Id
    charger_id: Id
    start_tick: Tick
    end_tick: Tick
    replaces_booking_id: Id | None = None
    validation: Literal["PASS", "WARN", "FAIL"]
    issues: list[ChargeScheduleIssue]
    charger_revision: Revision
    out_of_service_s: Annotated[FiniteFloat, Field(ge=0)]
    battery_at_start_kwh: Annotated[FiniteFloat, Field(ge=0)]
    battery_after_kwh: Annotated[FiniteFloat, Field(ge=0)]
    loops_after: Annotated[int, Field(ge=0)] | None
    min_in_service_vehicles: Annotated[int, Field(ge=0)]
    assumptions: list[str]
    is_estimate: Literal[True] = True


class DispatcherCommand(WireModel):
    contract_version: Annotated[str, Field(pattern=r"^2\.[0-9]+$")]
    command_id: Id
    idempotency_key: Id
    session_id: Id
    command_type: Literal["START", "PAUSE", "RESUME", "RESET", "ARCHIVE", "APPROVE_ROUTE", "APPROVE_INCIDENT_OPTION", "RESERVE_CHARGER",
                          "CONTINUE_SERVICE", "SCHEDULE_CHARGE", "CANCEL_CHARGE_BOOKING"]
    expected_epoch: Revision
    expected_revision: Revision
    target_type: Literal["SESSION", "ROUTE", "INCIDENT", "CHARGER", "VEHICLE"]
    target_id: Id
    payload: dict[str, Any]
    reason: Text | None = None

    @model_validator(mode="after")
    def reason_for_decision(self) -> "DispatcherCommand":
        if not supported_version(self.contract_version):
            raise ValueError("unsupported contract major version")
        if self.command_type in {"APPROVE_ROUTE", "APPROVE_INCIDENT_OPTION", "RESERVE_CHARGER", "CONTINUE_SERVICE",
                                 "SCHEDULE_CHARGE", "CANCEL_CHARGE_BOOKING"} and not self.reason:
            raise ValueError("decision command requires reason")
        return self


class Envelope(WireModel):
    contract_version: Annotated[str, Field(pattern=r"^2\.[0-9]+$")]
    session_id: Id
    session_epoch: Revision
    scene_revision: Id
    transform_revision: Id
    tick: Tick
    simulation_time_s: Annotated[float, Field(ge=0)]
    event_sequence: Revision
    source: Provenance

    @model_validator(mode="after")
    def supported_version(self) -> "Envelope":
        if not supported_version(self.contract_version):
            raise ValueError("unsupported contract major version")
        return self


def supported_version(version: str) -> bool:
    parts = version.split(".")
    return len(parts) == 2 and parts[0] == "2" and parts[1].isdigit()


class FullSnapshot(Envelope):
    message_type: Literal["FULL_SNAPSHOT"]
    snapshot_id: Id
    scenario: ScenarioRevision
    session: TrainingSession
    vehicles: list[FleetVehicle]
    routes: list[RouteRevision]
    disruptions: list[Disruption]
    incidents: list[Incident]
    candidates: list[Candidate]
    chargers: list[Charger]
    spatial: SpatialReference | None = None
    depots: list[Depot] = Field(default_factory=list)
    energy_forecasts: list[EnergyForecast] = Field(default_factory=list)
    charge_options: list[ChargeOption] = Field(default_factory=list)
    # Last revision issued per charge option ID, kept after withdrawal so a re-created
    # offer never reuses a revision an old choice could match.
    charge_option_revisions: dict[Id, Revision] = Field(default_factory=dict)
    # Shuttles whose operator chose CONTINUE, with the loops remaining at that decision;
    # the shuttle is offered a new battery decision only once it falls below that number.
    # A shuttle at zero loops cannot fall further, so it is never deferred: a 0 entry means
    # "not deferred" and is dropped on load (earlier builds stored them; they must still load).
    charge_decision_deferrals: dict[Id, Annotated[int, Field(ge=0)]] = Field(default_factory=dict)
    charge_bookings: list[ChargeBooking] = Field(default_factory=list)

    @field_validator("charge_decision_deferrals")
    @classmethod
    def drop_zero_deferrals(cls, value: dict[str, int]) -> dict[str, int]:
        return {vehicle_id: loops for vehicle_id, loops in value.items() if loops > 0}

    @model_validator(mode="after")
    def references(self) -> "FullSnapshot":
        if self.spatial is not None:
            if (self.scenario.spatial != self.spatial or self.spatial.scene_revision != self.scene_revision
                    or self.spatial.transform_revision != self.transform_revision):
                raise ValueError("SPATIAL_REVISION_MISMATCH")
            if any(v.spatial is None for v in self.vehicles):
                raise ValueError("MISSING_MANDATORY_SPATIAL_POSE")
            for vehicle in self.vehicles:
                pose = vehicle.spatial
                if pose.position_m != [vehicle.position.x_m, vehicle.position.y_m, vehicle.position.z_m]:
                    raise ValueError("BODY_CENTRE_POSITION_MISMATCH")
        elif self.scenario.spatial is not None or any(v.spatial is not None for v in self.vehicles):
            raise ValueError("MISSING_SPATIAL_HANDSHAKE")
        if self.session_id != self.session.session_id or self.session_epoch != self.session.session_epoch:
            raise ValueError("snapshot envelope/session mismatch")
        if self.scenario.revision_id != self.session.scenario_revision_id:
            raise ValueError("missing scenario revision reference")
        if self.scene_revision != self.scenario.scene_revision or self.transform_revision != self.scenario.transform_revision:
            raise ValueError("snapshot scene/transform revision mismatch")
        vehicles = {v.vehicle_id for v in self.vehicles}
        routes = {(r.route_id, r.revision) for r in self.routes}
        incidents = {i.incident_id for i in self.incidents}
        candidates = {c.candidate_id for c in self.candidates}
        for name, ids in (("vehicle", [v.vehicle_id for v in self.vehicles]), ("route", [r.route_id for r in self.routes]), ("incident", [i.incident_id for i in self.incidents]), ("candidate", [c.candidate_id for c in self.candidates]), ("charger", [c.charger_id for c in self.chargers])):
            if len(ids) != len(set(ids)):
                raise ValueError(f"duplicate {name} ID")
        for v in self.vehicles:
            if v.route_id is not None and (v.route_id, v.route_revision) not in routes:
                raise ValueError("missing vehicle route revision reference")
            if v.service_state == "CHARGING" and not any(v.vehicle_id in c.occupied_vehicle_ids for c in self.chargers):
                raise ValueError("charging vehicle has no occupied charger")
        for i in self.incidents:
            if ((i.vehicle_id is not None and i.vehicle_id not in vehicles)
                    or any(cid not in candidates for cid in i.candidate_ids)
                    or any(v not in vehicles for v in i.exposed_vehicle_ids)
                    or any(r.vehicle_id not in vehicles for r in i.responses)):
                raise ValueError("missing incident reference")
        for c in self.candidates:
            if c.incident_id not in incidents or (c.vehicle_id is not None and c.vehicle_id not in vehicles):
                raise ValueError("missing candidate incident reference")
        for c in self.chargers:
            if len(c.occupied_vehicle_ids) > c.port_count or any(v not in vehicles for v in c.occupied_vehicle_ids):
                raise ValueError("invalid charger occupancy")
        occupied = [vehicle_id for c in self.chargers for vehicle_id in c.occupied_vehicle_ids]
        if len(occupied) != len(set(occupied)):
            raise ValueError("vehicle occupies multiple chargers")
        if any(r.scene_revision != self.scene_revision for r in self.routes):
            raise ValueError("route scene revision mismatch")
        depot_ids = [d.depot_id for d in self.depots]
        if len(depot_ids) != len(set(depot_ids)):
            raise ValueError("duplicate depot ID")
        charger_ids = {c.charger_id for c in self.chargers}
        for depot in self.depots:
            if any(cid not in charger_ids for cid in depot.charger_ids):
                raise ValueError("depot references an unknown charger")
        depot_chargers = [cid for d in self.depots for cid in d.charger_ids]
        if len(depot_chargers) != len(set(depot_chargers)):
            raise ValueError("charger belongs to more than one depot")
        forecast_vehicles = [f.vehicle_id for f in self.energy_forecasts]
        if len(forecast_vehicles) != len(set(forecast_vehicles)):
            raise ValueError("more than one energy forecast for a vehicle")
        vehicle_ids = {v.vehicle_id for v in self.vehicles}
        known_depots = {d.depot_id for d in self.depots}
        for forecast in self.energy_forecasts:
            if forecast.vehicle_id not in vehicle_ids:
                raise ValueError("energy forecast for an unknown vehicle")
            if forecast.energy_model_revision != self.scenario.energy_model_revision:
                raise ValueError("forecast uses a different energy model than the scenario")
            if forecast.next_depot_id is not None and forecast.next_depot_id not in known_depots:
                raise ValueError("forecast references an unknown depot")
        option_ids = [o.option_id for o in self.charge_options]
        if len(option_ids) != len(set(option_ids)):
            raise ValueError("duplicate charge option ID")
        # A missing entry is allowed (2.x snapshots from before the history map): the active
        # offer's own revision is then its last issued one.
        if any(self.charge_option_revisions.get(o.option_id, o.revision) != o.revision for o in self.charge_options):
            raise ValueError("charge option revision differs from its last issued revision")
        if any(vehicle_id not in vehicle_ids for vehicle_id in self.charge_decision_deferrals):
            raise ValueError("charge decision deferral for an unknown vehicle")
        vehicles_by_id = {v.vehicle_id: v for v in self.vehicles}
        for option in self.charge_options:
            if option.vehicle_id not in vehicle_ids:
                raise ValueError("charge option for an unknown vehicle")
            if option.charger_id is not None and option.charger_id not in charger_ids:
                raise ValueError("charge option references an unknown charger")
            vehicle = vehicles_by_id[option.vehicle_id]
            if option.battery_after_kwh > vehicle.battery_capacity_kwh:
                raise ValueError("charge option battery_after_kwh exceeds the vehicle's capacity")
            # Without a charger the battery can only drain, never gain.
            if option.charger_id is None and option.battery_after_kwh > vehicle.battery_kwh:
                raise ValueError("charge option without a charger cannot gain battery")
            if option.min_in_service_vehicles is not None and option.min_in_service_vehicles > len(self.vehicles):
                raise ValueError("charge option min_in_service_vehicles exceeds the fleet size")
        booking_ids = [b.booking_id for b in self.charge_bookings]
        if len(booking_ids) != len(set(booking_ids)):
            raise ValueError("duplicate charge booking ID")
        live_vehicles = [b.vehicle_id for b in self.charge_bookings if b.status in {"SCHEDULED", "ACTIVE"}]
        if len(live_vehicles) != len(set(live_vehicles)):
            raise ValueError("vehicle has more than one live charge booking")
        for booking in self.charge_bookings:
            if booking.vehicle_id not in vehicle_ids or booking.charger_id not in charger_ids:
                raise ValueError("charge booking references an unknown vehicle or charger")
        return self


class Event(WireModel):
    event_id: Id
    event_sequence: Revision
    tick: Tick
    entity_type: Literal["SESSION", "VEHICLE", "ROUTE", "DISRUPTION", "INCIDENT", "CHARGER", "COMMAND", "ASSESSMENT"]
    entity_id: Id
    entity_revision: Revision
    event_type: Id
    source: Provenance
    data: dict[str, Any]


class EventReplay(Envelope):
    message_type: Literal["EVENT_REPLAY"]
    after_sequence: Revision
    events: list[Event]
    next_sequence: Revision
    has_more: bool

    @model_validator(mode="after")
    def ordered_events(self) -> "EventReplay":
        sequences = [e.event_sequence for e in self.events]
        if sequences != list(range(self.after_sequence + 1, self.after_sequence + 1 + len(sequences))):
            raise ValueError("event replay has a sequence gap")
        if self.next_sequence != (sequences[-1] if sequences else self.after_sequence) or self.next_sequence > self.event_sequence:
            raise ValueError("invalid replay cursor")
        return self


class CommandStatus(Envelope):
    message_type: Literal["COMMAND_STATUS"]
    command_id: Id
    idempotency_key: Id
    status: Literal["QUEUED", "APPLIED", "REJECTED", "NEEDS_RECONFIRMATION", "EXPIRED"]
    target_type: Literal["SESSION", "ROUTE", "INCIDENT", "CHARGER", "VEHICLE"]
    target_id: Id
    target_revision: Revision
    applied_tick: Tick | None
    result_event_id: Id | None
    failure_code: Id | None

    @model_validator(mode="after")
    def status_fields(self) -> "CommandStatus":
        if self.status == "APPLIED" and (self.applied_tick is None or self.result_event_id is None):
            raise ValueError("APPLIED requires applied_tick and result_event_id")
        if self.status == "REJECTED" and self.failure_code is None:
            raise ValueError("REJECTED requires failure_code")
        if self.status == "QUEUED" and (self.applied_tick is not None or self.result_event_id is not None):
            raise ValueError("QUEUED cannot claim application")
        return self


class ValidationFailure(WireModel):
    code: Literal["STALE_REVISION", "OPTION_EXPIRED", "UNKNOWN_CONSTRAINT", "NO_FEASIBLE_ROUTE", "CHARGER_CONFLICT", "ROLE_DENIED", "QUEUE_FULL", "SESSION_RECOVERING", "INVALID_REFERENCE", "INVALID_TRANSITION", "UNSUPPORTED_VERSION", "INVALID_PAYLOAD", "ASSESSMENT_NOT_READY"]
    message: Text
    field: str | None = None
    retryable: bool
    current_revision: Revision | None = None


class ErrorResponse(Envelope):
    message_type: Literal["ERROR"]
    error: ValidationFailure
    correlation_id: Id


Message = Annotated[Union[FullSnapshot, EventReplay, CommandStatus, ErrorResponse], Field(discriminator="message_type")]


def parse_message(value: dict[str, Any]) -> FullSnapshot | EventReplay | CommandStatus | ErrorResponse:
    from pydantic import TypeAdapter
    return TypeAdapter(Message).validate_python(value)


def validate_command_against_snapshot(command: DispatcherCommand, snapshot: FullSnapshot) -> ValidationFailure | None:
    """Contract preconditions; the actor must recheck these when applying a command."""
    def fail(code: str, message: str, current: int | None = None) -> ValidationFailure:
        return ValidationFailure(code=code, message=message, field="expected_revision",
                                 retryable=code in {"STALE_REVISION", "OPTION_EXPIRED"},
                                 current_revision=current)

    if command.session_id != snapshot.session_id:
        return fail("INVALID_REFERENCE", "Command targets a different session")
    if command.expected_epoch != snapshot.session_epoch:
        return fail("STALE_REVISION", "Session epoch changed", snapshot.session_epoch)
    collections = {
        "SESSION": {snapshot.session_id: snapshot.session.aggregate_revision},
        "ROUTE": {r.route_id: r.revision for r in snapshot.routes},
        "INCIDENT": {i.incident_id: i.aggregate_revision for i in snapshot.incidents},
        "CHARGER": {c.charger_id: c.aggregate_revision for c in snapshot.chargers},
        "VEHICLE": {v.vehicle_id: v.aggregate_revision for v in snapshot.vehicles},
    }
    current = collections[command.target_type].get(command.target_id)
    if current is None:
        return fail("INVALID_REFERENCE", "Command target does not exist")
    if current != command.expected_revision:
        return fail("STALE_REVISION", "Target aggregate revision changed", current)
    if command.command_type == "APPROVE_ROUTE":
        if command.target_type != "ROUTE" or snapshot.session.status != "READY":
            return fail("INVALID_TRANSITION", "Pre-trip route approval requires a READY session and route target")
        payload = command.payload
        closures = {d.disruption_id: d.aggregate_revision for d in snapshot.disruptions}
        if payload.get("closure_revisions") != closures:
            return fail("STALE_REVISION", "Closure revision changed")
        edge_ids = payload.get("directed_edge_ids")
        targets = payload.get("target_vehicle_ids")
        if not isinstance(edge_ids, list) or not edge_ids or not all(isinstance(e, str) and e for e in edge_ids):
            return fail("INVALID_PAYLOAD", "Approved route requires directed edge IDs")
        if not isinstance(targets, list) or not targets or not all(isinstance(v, str) and v for v in targets) or len(targets) != len(set(targets)):
            return fail("INVALID_PAYLOAD", "Approval requires distinct target shuttle IDs")
        assigned = {v.vehicle_id for v in snapshot.vehicles if v.route_id == command.target_id and v.route_revision == current}
        if set(targets) != assigned:
            return fail("INVALID_REFERENCE", "Approval target set must equal assigned shuttles for this route pattern")
        closed = {edge for d in snapshot.disruptions
                  if d.active_from_tick <= snapshot.tick and
                  (d.active_until_tick is None or snapshot.tick < d.active_until_tick)
                  for edge in d.affected_edge_ids}
        if any(edge in closed for edge in edge_ids):
            return fail("NO_FEASIBLE_ROUTE", "Approved route crosses an active closure")
    if command.command_type == "APPROVE_INCIDENT_OPTION":
        candidate_id = command.payload.get("candidate_id")
        candidate = next((c for c in snapshot.candidates if c.candidate_id == candidate_id and c.incident_id == command.target_id), None)
        if candidate is None:
            return fail("INVALID_REFERENCE", "Candidate does not belong to target incident")
        if candidate.expires_at_tick < snapshot.tick:
            return fail("OPTION_EXPIRED", "Candidate expired")
        if candidate.validation in {"FAIL", "UNKNOWN"}:
            return fail("UNKNOWN_CONSTRAINT", "Candidate cannot be approved")
        for dependency_id, revision in candidate.dependency_revisions.items():
            actual = next((i.aggregate_revision for i in snapshot.incidents if i.incident_id == dependency_id), None)
            if actual is None or actual != revision:
                return fail("STALE_REVISION", "Candidate dependency changed", actual)
    if command.command_type in {"RESERVE_CHARGER", "CONTINUE_SERVICE"}:
        option = next((o for o in snapshot.charge_options if o.option_id == command.payload.get("option_id")), None)
        if option is None or option.expires_at_tick < snapshot.tick:
            return fail("OPTION_EXPIRED", "Charge option is no longer offered")
        if command.command_type == "RESERVE_CHARGER" and (option.action != "CHARGE_NOW" or option.charger_id != command.target_id):
            return fail("INVALID_PAYLOAD", "Charge option does not reserve this charger")
        if command.command_type == "CONTINUE_SERVICE" and (option.action != "CONTINUE" or option.vehicle_id != command.target_id):
            return fail("INVALID_PAYLOAD", "Charge option does not keep this shuttle in service")
        if command.payload.get("option_revision") != option.revision:
            return fail("STALE_REVISION", "Charge option changed since it was chosen", option.revision)
        if option.validation in {"FAIL", "UNKNOWN"}:
            return fail("UNKNOWN_CONSTRAINT", "Charge option cannot be approved")
        current = {v.vehicle_id: v.aggregate_revision for v in snapshot.vehicles}
        current.update({c.charger_id: c.aggregate_revision for c in snapshot.chargers})
        for dependency_id, revision in option.dependency_revisions.items():
            if current.get(dependency_id) != revision:
                return fail("STALE_REVISION", "Charge option dependency changed", current.get(dependency_id))
    return None
