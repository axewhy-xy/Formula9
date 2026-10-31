"""
Atomic domain-to-actor commit boundary for BE-M2-02 through BE-M2-05 integration.

Author: Nur Syazliyarni Binte Zazali
Date Created: 28/10/2026
Last Modified: 28/10/2026

Description:
    Runs existing route, incident and battery services under the live session
    lock. Their reads use the staged actor boundary; acceptance remains durable.
    Application synchronizes private execution and journals public state, domain
    events, command outcome and checkpoint together before installing the actor.
    Failed or rejected writes never install staged consequences.
"""

from copy import deepcopy
from uuid import uuid4

from contracts.v2.models import Event
from src.persistence.training_store import TrainingStoreError
from src.services.battery_decision import submit_battery_decision
from src.services.incidents import IncidentService


def session_incidents(actor):
    """Keep random incident configuration isolated to the session actor."""
    service = actor.definition.incidents
    selection = getattr(actor, "random_incident", None)
    if not selection:
        # Disable the original authored incident: wait for the random event
        # on the first shuttle's approved route instead.
        config = deepcopy(service.config)
        config["trigger_tick"] = 10**12
        return IncidentService(actor.definition.routes, fixture=config)
    config = deepcopy(service.config)
    config.update(selection)
    # Existing decision and option machinery remains the authoritative writer.
    return IncidentService(actor.definition.routes, fixture=config)


class ActorCommit:
    """TrainingStore-compatible unit of work for the existing domain services.

    Construct only while holding the live session lock, with a detached actor.
    Reads/history/receipts delegate to the real store. The two write methods
    inject the actor checkpoint into the same SQLite transaction as the domain
    operation, preserving the services' existing validation and retry semantics.
    """

    def __init__(self, store, actor, checkpoint, *, current=True):
        self.store, self.actor, self.checkpoint = store, actor, checkpoint
        self.baseline = store.snapshot(actor.session_id)
        self.current = current

    def __getattr__(self, name):
        return getattr(self.store, name)

    def snapshot(self, session_id):
        if session_id != self.actor.session_id:
            return self.store.snapshot(session_id)
        if not self.current:
            return self.baseline.model_copy(deep=True)
        result = self.actor.generate_snapshot()
        result.snapshot_id = self.baseline.snapshot_id
        result.event_sequence = self.baseline.event_sequence
        result.session.status = self.baseline.session.status
        result.session.aggregate_revision = self.baseline.session.aggregate_revision
        return result

    def _prepare(self, proposed, events):
        self.actor.synchronize(proposed)
        result = self.actor.generate_snapshot()
        result.snapshot_id = self.baseline.snapshot_id
        events = [*self.actor.pending_events, *events]
        ordered = tuple(event.model_copy(update={"event_sequence": self.baseline.event_sequence+index+1,
            "tick": result.tick}) for index, event in enumerate(events))
        result.event_sequence = self.baseline.event_sequence+len(ordered)
        self.actor.pending_events = []
        self.actor.domain = result.model_copy(deep=True)
        return result, ordered

    def apply(self, command_id, proposed=None, domain_events=(), **kwargs):
        if proposed is None:
            return self.store.apply(command_id, **kwargs)
        try:
            proposed, domain_events = self._prepare(proposed, domain_events)
        except TrainingStoreError as error:
            return self.store.apply(command_id, rejection_code=error.code)
        kwargs.setdefault("expected_snapshot_id", self.baseline.snapshot_id)
        result = self.store.apply(command_id, proposed, domain_events,
            runtime_state=self.checkpoint(self.actor), **kwargs)
        return result

    def commit_snapshot(self, proposed, domain_events=(), **kwargs):
        proposed, domain_events = self._prepare(proposed, domain_events)
        kwargs.setdefault("expected_snapshot_id", self.baseline.snapshot_id)
        result = self.store.commit_snapshot(proposed, domain_events,
            runtime_state=self.checkpoint(self.actor), **kwargs)
        self.actor.domain = result.model_copy(deep=True)
        return result


def submit_workflow(store, actor, actor_id, command, checkpoint):
    """Apply a decision to one detached actor at its current logical boundary."""
    staged = deepcopy(actor)
    unit = ActorCommit(store, staged, checkpoint)
    kind = command.command_type
    if kind in {"START", "PAUSE", "RESUME", "ARCHIVE"}:
        receipt = store.accept(actor_id, command)
        if receipt.status != "QUEUED":
            return receipt, None
        _, accepted = store.command_record(command.session_id, actor_id, command.idempotency_key)
        snapshot = unit.snapshot(actor.session_id)
        if kind == "START":
            blocked = {edge for d in snapshot.disruptions if d.active_from_tick <= snapshot.tick
                and (d.active_until_tick is None or snapshot.tick < d.active_until_tick) for edge in d.affected_edge_ids}
            assigned = {v.route_id for v in snapshot.vehicles}
            if any(blocked.intersection(r.directed_edge_ids) for r in snapshot.routes if r.route_id in assigned):
                return store.apply(accepted.command_id, rejection_code="UNKNOWN_CONSTRAINT"), None
        snapshot.session.status = {"START": "RUNNING", "PAUSE": "PAUSED", "RESUME": "RUNNING", "ARCHIVE": "ARCHIVED"}[kind]
        snapshot.session.aggregate_revision += 1
        snapshot.event_sequence += 1
        event = Event(event_id=uuid4().hex, event_sequence=snapshot.event_sequence, tick=snapshot.tick,
            event_type=f"SESSION_{snapshot.session.status}", entity_type="SESSION", entity_id=actor.session_id,
            entity_revision=snapshot.session.aggregate_revision, source=snapshot.source,
            data={"status": snapshot.session.status})
        result = unit.apply(accepted.command_id, snapshot, (event,))
    elif kind == "APPROVE_INCIDENT_OPTION":
        result = session_incidents(staged).decide(unit, actor_id, command)
    elif kind in {"RESERVE_CHARGER", "CONTINUE_SERVICE"}:
        # Preserve final-outcome retries, but never execute a fresh operational
        # decision against paused/ready state or a changed depot reachability.
        receipt = store.accept(actor_id, command, defer_preconditions=True)
        if receipt.status != "QUEUED":
            return receipt, None
        if unit.baseline.session.status != "RUNNING":
            return store.apply(receipt.command_id, rejection_code="INVALID_TRANSITION"), None
        if kind == "RESERVE_CHARGER":
            snapshot = unit.snapshot(actor.session_id)
            option = next((o for o in snapshot.charge_options if o.option_id == command.payload.get("option_id")), None)
            forecast = next((f for f in snapshot.energy_forecasts if option and f.vehicle_id == option.vehicle_id), None)
            if forecast is not None and not forecast.reaches_depot_above_reserve:
                return store.apply(receipt.command_id, rejection_code="NO_FEASIBLE_ROUTE"), None
        result = submit_battery_decision(unit, actor_id, command, rebuild_decisions=staged.battery_decisions)
    else:
        raise TrainingStoreError("INVALID_TRANSITION", "Create a new attempt to reset this scenario")
    return result, staged if result.status == "APPLIED" else None


def approve_workflow_route(store, actor, actor_id, request, checkpoint):
    staged = deepcopy(actor)
    unit = ActorCommit(store, staged, checkpoint)
    result = staged.definition.routes.approve(unit, actor_id, actor.session_id, request)
    return result, staged if result.status == "APPLIED" else None


def publish_workflow(store, actor, snapshot, checkpoint):
    unit = ActorCommit(store, actor, checkpoint, current=False)
    service = session_incidents(actor)
    selected = getattr(actor, "random_incident", None)
    if selected and snapshot.tick <= selected["trigger_tick"] + 2:
        print(f"[INCIDENT DEBUG] PUBLISH attempt tick={snapshot.tick} "
              f"id={selected['incident_id']} route={selected['route_id']} "
              f"edge={selected['affected_edge_ids']}", flush=True)
    try:
        committed = service.publish(unit, snapshot)
    except Exception as error:
        print(f"[INCIDENT DEBUG] PUBLISH FAILED tick={snapshot.tick}: "
              f"{type(error).__name__}: {error}", flush=True)
        raise
    if selected and snapshot.tick <= selected["trigger_tick"] + 2:
        print(f"[INCIDENT DEBUG] PUBLISHED tick={committed.tick} "
              f"incidents={[(i.incident_id, i.state) for i in committed.incidents]}", flush=True)
    return committed
