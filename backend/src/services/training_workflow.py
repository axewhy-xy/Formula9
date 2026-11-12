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
import logging
from uuid import uuid4

from contracts.v2.models import Event
from src.persistence.training_store import TrainingStoreError
from src.services.battery_decision import submit_battery_decision
from src.services.charge_booking import BOOKING_COMMANDS, submit_booking_command

logger = logging.getLogger(__name__)


def session_incidents(actor, incident_id=None):
    """The service for one of the session's incidents, built from the scenario's shared rules and that
    incident's own checkpointed record; never from shared mutable globals or another incident's fields."""
    service = actor.definition.incidents
    selections = getattr(actor, "random_incidents", [])
    if not selections and getattr(actor, "random_incident", None):
        selections = [actor.random_incident]
    selection = next((item for item in selections if item["incident_id"] == incident_id), None) if incident_id else (selections[-1] if selections else None)
    if selection is None:
        # A settled incident left the active list; a retried or late decision still targets it. With no
        # incident at all, the record names no shuttle and never triggers, so staging it changes nothing.
        selection = {"incident_id": incident_id or service.config["incident_id"]}
    return service.instance(selection)


def _settled(item, snapshot):
    """A settled incident with an expired closure has no remaining state transitions."""
    incident = next((i for i in snapshot.incidents if i.incident_id == item["incident_id"]), None)
    if incident is None or not (incident.state == "RESOLVED" or
            (incident.state == "UNRESOLVED" and item.get("requires_guidance", True))):
        return False
    disruption = next((d for d in snapshot.disruptions
                       if d.disruption_id == item.get("disruption_id")), None)
    return disruption is None or (disruption.active_until_tick is not None
                                  and snapshot.tick >= disruption.active_until_tick)


class ActorCommit:
    """TrainingStore-compatible unit of work for the existing domain services.

    Construct only while holding the live session lock, with a detached actor.
    Reads/history/receipts delegate to the real store. The two write methods
    inject the actor checkpoint into the same SQLite transaction as the domain
    operation, preserving the services' existing validation and retry semantics.

    baseline is the session's last committed snapshot when the caller holds it
    (a publication does); otherwise it is read from the store. A stale one is
    refused by the commit's expected_snapshot_id, as a stale read would be.
    """

    def __init__(self, store, actor, checkpoint, *, current=True, baseline=None):
        self.store, self.actor, self.checkpoint = store, actor, checkpoint
        self.baseline = baseline if baseline is not None else store.snapshot(actor.session_id)
        self.current = current

    def __getattr__(self, name):
        return getattr(self.store, name)

    def snapshot(self, session_id):
        if session_id != self.actor.session_id:
            return self.store.snapshot(session_id)
        if not self.current:
            # Only a publication reads the committed boundary (IncidentService.publish and stage), and
            # only reads it: no copy
            return self.baseline
        result = self.actor.generate_snapshot()
        result.snapshot_id = self.baseline.snapshot_id
        result.event_sequence = self.baseline.event_sequence
        result.session.status = self.baseline.session.status
        result.session.aggregate_revision = self.baseline.session.aggregate_revision
        return result

    def _prepare(self, proposed, events, *, copy_domain=True):
        # proposed is the caller's to give: the actor adopts it as its domain. Without copy_domain the
        # domain is filled in and published as it is; otherwise the publication is a fresh copy and
        # the caller's snapshot is not changed by the store
        self.actor.synchronize(proposed, adopt=True)
        self.actor.note_decisions(events)
        result = self.actor.generate_snapshot(in_place=not copy_domain)
        result.snapshot_id = self.baseline.snapshot_id
        events = [*self.actor.pending_events, *events]
        ordered = tuple(event.model_copy(update={"event_sequence": self.baseline.event_sequence+index+1,
            "tick": result.tick}) for index, event in enumerate(events))
        result.event_sequence = self.baseline.event_sequence+len(ordered)
        self.actor.pending_events = []
        # The checkpoint taken next carries the domain. store.apply changes the snapshot it is given, so
        # there the domain is a copy; commit_snapshot leaves its snapshot alone (result is the domain
        # already) and replaces the domain with the committed one once it is durable
        if copy_domain:
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
        proposed, domain_events = self._prepare(proposed, domain_events, copy_domain=False)
        kwargs.setdefault("expected_snapshot_id", self.baseline.snapshot_id)
        result = self.store.commit_snapshot(proposed, domain_events,
            runtime_state=self.checkpoint(self.actor), **kwargs)
        # The committed snapshot is both the actor's domain and the session's baseline: neither is
        # changed in place once installed (a tick, command or publication stages a deep copy of the
        # actor and only reads the baseline)
        self.actor.domain = result
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
        result = session_incidents(staged, command.target_id).decide(unit, actor_id, command)
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
    elif kind in BOOKING_COMMANDS:
        result = submit_booking_command(unit, actor_id, command, charge_s=staged.definition.data["charge_s"],
                                        charge_time_scale=staged.definition.data.get("charge_time_scale"),
                                        rebuild_decisions=staged.battery_decisions)
    else:
        raise TrainingStoreError("INVALID_TRANSITION", "Create a new attempt to reset this scenario")
    return result, staged if result.status == "APPLIED" else None


def approve_workflow_route(store, actor, actor_id, request, checkpoint):
    staged = deepcopy(actor)
    unit = ActorCommit(store, staged, checkpoint)
    result = staged.definition.routes.approve(unit, actor_id, actor.session_id, request)
    return result, staged if result.status == "APPLIED" else None


def publish_workflow(store, actor, snapshot, checkpoint, baseline=None):
    """Publish incident updates with per-incident diagnostics and durable checkpoints.

    The caller gives up snapshot, which becomes the publication. baseline, when given, is the
    session's last committed snapshot, as the caller holds it; it is only read."""
    selections = getattr(actor, "random_incidents", [])
    if not selections and getattr(actor, "random_incident", None):
        selections = [actor.random_incident]

    if not selections:
        return session_incidents(actor).publish(
            ActorCommit(store, actor, checkpoint, current=False, baseline=baseline), snapshot, owned=True
        )

    # Keep only live selections in every checkpoint and actor copy. The durable
    # event log keeps each settled incident's evidence.
    actor.random_incidents = [item for item in selections if not _settled(item, snapshot)]
    result = snapshot
    events = []
    unit = ActorCommit(store, actor, checkpoint, current=False, baseline=baseline)
    for item in actor.random_incidents:
        incident_id = item["incident_id"]
        existing = next((i for i in result.incidents if i.incident_id == incident_id), None)
        try:
            if result.tick >= item.get("trigger_tick", 10**12) and (
                existing is None or result.tick % 100 == 0
            ):
                logger.debug("[INCIDENT FLOW] BEGIN tick=%s id=%s state=%s guidance=%s", result.tick, incident_id,
                             getattr(existing, "state", None), item.get("requires_guidance"))
            # result is this publication's own: the snapshot the caller gave up, or an earlier stage's
            result, staged = session_incidents(actor, incident_id).stage(unit, result, owned=True)
            events.extend(staged)
            if result.tick >= item.get("trigger_tick", 10**12) and (
                existing is None or result.tick % 100 == 0
            ):
                updated = next((i for i in result.incidents if i.incident_id == incident_id), None)
                logger.debug("[INCIDENT FLOW] STAGED tick=%s id=%s state=%s event_sequence=%s",
                             result.tick, incident_id, getattr(updated, "state", None), result.event_sequence)
        except Exception:
            logger.exception("[INCIDENT FLOW] FAILED tick=%s id=%s", result.tick, incident_id)
            raise
    # One commit per boundary, however many incidents are live: a second commit at
    # the same tick with no events of its own would be refused as stale. If all were
    # settled/skipped, the actor still needs its movement snapshot and checkpoint.
    return unit.commit_snapshot(result, tuple(events))
