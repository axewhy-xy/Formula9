"""One durable, server-owned clock per admitted fleet attempt."""

from __future__ import annotations

import asyncio
from collections.abc import Coroutine
from contextvars import Context
from copy import deepcopy
from dataclasses import asdict, dataclass, field
import hashlib
import logging
from typing import Any, TypeVar
from uuid import uuid4

from contracts.v2.models import CommandStatus, DispatcherCommand, Event, FullSnapshot
from contracts.v2.sessions import CreateSessionRequest
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.core.training_telemetry import SessionProgress, boundary
from src.schemas.shuttle_data import Shuttle
from src.services.asset_loader import load_routes_from_json
from src.services.session_actor import SessionActor
from src.services.integrated_actor import IntegratedSessionActor, SCENARIO_SELECTOR
from src.services.training_workflow import approve_workflow_route, publish_workflow, submit_workflow
from src.services.spatial_session_actor import MotionLimits, SpatialSessionActor
from src.services.training_scene import SceneValidationError

logger = logging.getLogger(__name__)
SCENARIO_REVISION = "scenario-m2-r1"
SPATIAL_SCENARIO_REVISION = "scenario-m2-road-3d-r1"
LIFECYCLE = {"START": "RUNNING", "PAUSE": "PAUSED", "RESUME": "RUNNING", "ARCHIVE": "ARCHIVED"}
T = TypeVar("T")


async def wait_for_deadline(trace, deadline_ns, **tags):
    """Await the high-resolution deadline, recording actual asyncio wake-ups.

    Keep the deadline recheck: an event loop may invoke a timer early according
    to its own clock resolution. Profiling observes this policy without changing
    the event-loop clock, OS timer settings or authoritative logical step.
    """
    started = trace.clock()
    calls = early = 0
    loop = asyncio.get_running_loop()
    while (remaining := deadline_ns-trace.clock()) > 0:
        with trace.span("tick.sleep", deadline_ns=deadline_ns, requested_ms=remaining/1e6,
                        loop_time_before_s=loop.time(), **tags) as fields:
            await asyncio.sleep(remaining/1e9)
            woke = trace.clock()
            fields.update(woke_early=woke < deadline_ns, wake_lateness_ms=(woke-deadline_ns)/1e6,
                          loop_time_after_s=loop.time())
        calls += 1
        early += woke < deadline_ns
    return {"deadline_ns": deadline_ns, "wait_started_ns": started,
            "wait_ms": (trace.clock()-started)/1e6, "sleep_calls": calls,
            "early_wakeups": early, "wait_enter_lateness_ms": max(0, started-deadline_ns)/1e6}


def checkpoint(actor: SessionActor) -> dict:
    state = {
        "version": 1, "seed": actor.seed, "scenario_revision": actor.scenario_revision,
        "tick": actor.current_tick, "command_tick": actor.command_tick,
        "shuttles": [s.model_dump(mode="json") for s in actor.shuttles],
        "rng": actor.rng.getstate(),
    }
    if isinstance(actor, SpatialSessionActor):
        state.update(version=2, controller=actor.limits.revision, spatial=actor.reference.model_dump(),
                     surface_ids=list(actor.surface_ids), vehicle_ids=[b.entity_id for b in actor.bodies],
                     limits=asdict(actor.limits), spatial_diagnostics=deepcopy(actor.spatial_diagnostics),
                     spatial_work=actor.spatial_work)
    elif isinstance(actor, IntegratedSessionActor):
        state.update(version=3, workflow=actor.workflow_state())
    return state


def _tuples(value):
    return tuple(_tuples(item) for item in value) if isinstance(value, list) else value


@dataclass
class LiveSession:
    actor: SessionActor
    baseline: FullSnapshot | None = None
    task: asyncio.Task | None = None
    lock: asyncio.Lock = field(default_factory=asyncio.Lock)
    failed: bool = False
    failure_committed: bool = False
    progress: SessionProgress = field(default_factory=SessionProgress)


class TrainingSessionService:
    MAX_SESSIONS = 16
    FAILURE_RETRY_S = 0.25

    def __init__(self, store: TrainingStore):
        self.store = store
        self.sessions: dict[str, LiveSession] = {}
        self.closed = False
        self._initialized = False
        self._initialize_lock = asyncio.Lock()
        self._creation_lock = asyncio.Lock()
        self._operations: set[asyncio.Task] = set()
        self._tasks: set[asyncio.Task] = set()

    async def _complete(self, operation: Coroutine[Any, Any, T]) -> T:
        # A disconnected caller must not cancel work between its durable commit
        # and actor installation. Shutdown drains these operations before SQLite.
        task = asyncio.create_task(operation)
        self._operations.add(task)
        self.store.trace.high_water("live_pending_operations", len(self._operations))
        task.add_done_callback(self._operation_done)
        return await asyncio.shield(task)

    def _operation_done(self, task: asyncio.Task) -> None:
        self._operations.discard(task)
        if not task.cancelled():
            task.exception()  # Also consume failures after the caller disconnects.

    async def initialize(self) -> None:
        if not self._initialized:
            await self._complete(self._initialize())

    async def _initialize(self) -> None:
        async with self._initialize_lock:
            if self._initialized:
                return
            with self.store.trace.span("runtime.recovery") as fields:
                self.sessions = await asyncio.to_thread(self._restore)
                fields.update(restored_sessions=len(self.sessions))
            self._initialized = True
            for session in self.sessions.values():
                self._start(session)

    def _restore(self) -> dict[str, LiveSession]:
        sessions = {}
        for session_id in self.store.runtime_ids():
            try:
                snapshot, state = self.store.runtime(session_id)
                with self.store.trace.span("runtime.session_recovery", **boundary(snapshot)):
                    session = self._restore_session(snapshot, state)
            except (TrainingStoreError, SceneValidationError, ValueError, KeyError, TypeError, IndexError, OverflowError) as error:
                if isinstance(error, TrainingStoreError) and error.code != "CORRUPT_CHECKPOINT":
                    raise
                logger.exception("Cannot recover training runtime for %s", session_id)
                self._commit_failure(session_id, reason="CORRUPT_CHECKPOINT")
                continue
            if len(sessions) >= self.MAX_SESSIONS:
                raise TrainingStoreError("QUEUE_FULL", "Too many recoverable training sessions")
            sessions[session_id] = session
        return sessions

    @staticmethod
    def _restore_session(snapshot: FullSnapshot, state: dict) -> LiveSession:
        if any(type(state[key]) is not int or state[key] < 0 for key in ("tick", "command_tick")):
            raise TrainingStoreError("CORRUPT_CHECKPOINT", "Actor clocks must be nonnegative integers")
        if state["version"] == 3 and snapshot.spatial is None:
            actor = IntegratedSessionActor(seed=state["seed"], session_id=snapshot.session_id)
        elif state["version"] == 2:
            actor = SpatialSessionActor(seed=state["seed"], session_id=snapshot.session_id,
                                        surface_ids=tuple(state["surface_ids"]), vehicle_ids=tuple(state["vehicle_ids"]),
                                        limits=MotionLimits(**state["limits"]))
            if state["controller"] != actor.limits.revision or state["spatial"] != actor.reference.model_dump():
                raise TrainingStoreError("CORRUPT_CHECKPOINT", "Spatial geometry revision is unavailable")
            actor.restore_spatial_state(state)
        elif state["version"] == 1 and snapshot.spatial is None:
            actor = SessionActor(seed=state["seed"], route=load_routes_from_json(), session_id=snapshot.session_id)
        else:
            raise TrainingStoreError("CORRUPT_CHECKPOINT", "Actor checkpoint version is unavailable")
        if actor.scenario_revision != state["scenario_revision"]:
            raise TrainingStoreError("CORRUPT_CHECKPOINT", "Actor revision is unavailable for recovery")
        actor.current_tick = state["tick"]
        actor.command_tick = state["command_tick"]
        actor.timestamp_second = actor.current_tick * actor.STEP_S
        actor.session_epoch = snapshot.session_epoch
        actor.command_revision = snapshot.session.aggregate_revision
        actor.is_paused = snapshot.session.status != "RUNNING"
        actor.shuttles = [Shuttle.model_validate(item) for item in state["shuttles"]]
        if isinstance(actor, IntegratedSessionActor):
            actor.restore_workflow(state["workflow"], snapshot)
        if isinstance(actor, SpatialSessionActor):
            actor.validate_restored_shuttles()
        actor.rng.setstate(_tuples(state["rng"]))
        restored = actor.generate_snapshot()
        if (restored.tick != snapshot.tick or restored.vehicles != snapshot.vehicles
                or restored.scenario != snapshot.scenario):
            raise TrainingStoreError("CORRUPT_CHECKPOINT", "Actor and public checkpoint disagree")
        return LiveSession(actor, baseline=snapshot)

    def _start(self, session: LiveSession) -> None:
        if not self.closed:
            session.progress.observe(self.store.trace, session.actor, committed=session.baseline)
            session.task = asyncio.create_task(self._run(session), name=f"training-session-{session.actor.session_id}",
                                               context=Context())
            self._tasks.add(session.task)
            session.task.add_done_callback(self._tasks.discard)

    async def create(self, request: CreateSessionRequest, actor_id: str, key: str) -> FullSnapshot:
        await self.initialize()
        return await self._complete(self._create(request, actor_id, key))

    async def _create(self, request: CreateSessionRequest, actor_id: str, key: str) -> FullSnapshot:
        async with self._creation_lock:
            digest = hashlib.sha256(request.model_dump_json().encode()).hexdigest()
            prior = await asyncio.to_thread(self.store.creation, actor_id, key, digest)
            if prior is not None:
                return prior
            if request.scenario_revision_id not in {SCENARIO_REVISION, SPATIAL_SCENARIO_REVISION, SCENARIO_SELECTOR}:
                raise TrainingStoreError("INVALID_REFERENCE", "Scenario revision is not available")
            if self.closed or len(self.sessions) >= self.MAX_SESSIONS:
                raise TrainingStoreError("QUEUE_FULL", "Training session capacity reached")
            snapshot, actor = await asyncio.to_thread(self._create_initial, request, actor_id, key, digest)
            session = LiveSession(actor, baseline=snapshot.model_copy(deep=True))
            self.sessions[actor.session_id] = session
            self._start(session)
            return snapshot

    def _create_initial(self, request: CreateSessionRequest, actor_id: str, key: str,
                        digest: str) -> tuple[FullSnapshot, SessionActor]:
        if request.scenario_revision_id == SCENARIO_SELECTOR:
            actor = IntegratedSessionActor(seed=request.seed, session_id=str(uuid4()))
        elif request.scenario_revision_id == SPATIAL_SCENARIO_REVISION:
            actor = SpatialSessionActor(seed=request.seed, session_id=str(uuid4()))
        else:
            actor = SessionActor(seed=request.seed, route=load_routes_from_json(), session_id=str(uuid4()))
        actor.is_paused = True
        snapshot = actor.generate_snapshot()
        snapshot.session.status = "READY"
        snapshot = self.store.create_session(snapshot, runtime_state=checkpoint(actor), creation=(actor_id, key, digest))
        return snapshot, actor

    def owns(self, session_id: str) -> bool:
        return session_id in self.sessions

    async def delete(self, session_id: str) -> None:
        await self.initialize()
        await self._complete(self._delete(session_id))

    async def _delete(self, session_id: str) -> None:
        if self.owns(session_id):
            raise TrainingStoreError("INVALID_TRANSITION", "Archive the session before permanently deleting it")
        await asyncio.to_thread(self.store.delete_session, session_id)

    async def compact(self) -> dict:
        await self.initialize()
        return await self._complete(self._compact())

    async def _compact(self) -> dict:
        # Admission must not install a new actor while VACUUM owns the writer.
        async with self._creation_lock:
            if self.sessions:
                raise TrainingStoreError("INVALID_TRANSITION", "Archive all sessions before reclaiming disk space")
            return await asyncio.to_thread(self.store.compact)

    def _check_available(self, session_id: str) -> None:
        session = self.sessions.get(session_id)
        if session is not None and not session.failure_committed and (
                session.failed or session.task is None or session.task.done()):
            raise TrainingStoreError("SESSION_RECOVERING", "Training runtime is unavailable")

    async def snapshot(self, session_id: str) -> FullSnapshot:
        await self.initialize()
        self._check_available(session_id)
        snapshot = await asyncio.to_thread(self.store.snapshot, session_id)
        self._check_available(session_id)
        return snapshot

    @staticmethod
    def _snapshot(actor: SessionActor, baseline: FullSnapshot, status: str) -> FullSnapshot:
        snapshot = actor.generate_snapshot()
        snapshot.event_sequence = baseline.event_sequence
        snapshot.session.status = status
        snapshot.session.aggregate_revision = baseline.session.aggregate_revision
        if isinstance(actor, IntegratedSessionActor):
            snapshot.snapshot_id = baseline.snapshot_id
        return snapshot

    async def approve_route(self, actor_id: str, session_id: str, request) -> CommandStatus:
        await self.initialize()
        with self.store.trace.span("command.dispatch", command_id=request.command_id,
                                   command_type="APPROVE_ROUTE", session_id=session_id) as fields:
            result = await self._complete(self._approve_route(actor_id, session_id, request))
            fields.update(**boundary(result), command_id=result.command_id, outcome=result.status,
                          failure_code=result.failure_code)
            return result

    async def _approve_route(self, actor_id: str, session_id: str, request) -> CommandStatus:
        session = self.sessions.get(session_id)
        if session is None or not isinstance(session.actor, IntegratedSessionActor):
            raise TrainingStoreError("UNKNOWN_CONSTRAINT", "Live actor has no admitted route-edit fixture")
        async with session.lock:
            self._check_available(session_id)
            if self.closed:
                raise TrainingStoreError("SESSION_RECOVERING", "Training runtime is unavailable")
            result, staged = await asyncio.to_thread(approve_workflow_route, self.store, session.actor,
                                                     actor_id, request, checkpoint)
            if staged is not None:
                session.actor, session.baseline = staged, None
            session.progress.observe(self.store.trace, session.actor, committed=result)
            return result

    @staticmethod
    def _event(snapshot: FullSnapshot, event_type: str) -> Event:
        return Event(
            event_id=str(uuid4()), event_sequence=snapshot.event_sequence + 1, tick=snapshot.tick,
            event_type=event_type, entity_type="SESSION", entity_id=snapshot.session_id,
            entity_revision=snapshot.session.aggregate_revision, source=snapshot.source,
            data={"status": snapshot.session.status},
        )

    async def submit(self, actor_id: str, command: DispatcherCommand) -> CommandStatus:
        await self.initialize()
        with self.store.trace.span("command.dispatch", command_id=command.command_id,
                                   command_type=command.command_type, session_id=command.session_id,
                                   session_epoch=command.expected_epoch) as fields:
            result = await self._complete(self._submit(actor_id, command))
            fields.update(**boundary(result), command_id=result.command_id, outcome=result.status,
                          failure_code=result.failure_code)
            return result

    async def _submit(self, actor_id: str, command: DispatcherCommand) -> CommandStatus:
        session = self.sessions.get(command.session_id)
        if session is None:
            # Terminal runtimes release their slot, but their committed command
            # receipts remain available (including a retry of ARCHIVE).
            return await asyncio.to_thread(self.store.accept, actor_id, command)
        async with session.lock:
            unavailable = session.failed or session.task is None or session.task.done() or self.closed
            result, staged = await asyncio.to_thread(self._submit_staged, session.actor, actor_id, command, unavailable)
            if staged is not None:
                session.actor = staged
                # apply() appends the command outcome and assigns a new snapshot
                # ID. Refresh that boundary at the next presentation commit.
                session.baseline = None
            session.progress.observe(self.store.trace, session.actor, committed=result,
                                     running=not session.actor.is_paused and not session.failed and not self.closed)
            if staged is not None and command.command_type == "ARCHIVE":
                self.sessions.pop(command.session_id, None)
                session.task.cancel()
            return result

    def _submit_staged(self, actor: SessionActor, actor_id: str, command: DispatcherCommand,
                       unavailable: bool) -> tuple[CommandStatus, SessionActor | None]:
        if command.command_type not in LIFECYCLE and not isinstance(actor, IntegratedSessionActor):
            raise TrainingStoreError("INVALID_TRANSITION", "Create a new attempt to reset this scenario")
        if unavailable:
            # Replays of committed outcomes remain readable after runtime failure.
            # Do not journal a new command against a runtime that cannot apply it.
            try:
                self.store.command_record(command.session_id, actor_id, command.idempotency_key)
            except TrainingStoreError as error:
                if error.code != "INVALID_REFERENCE":
                    raise
                raise TrainingStoreError("SESSION_RECOVERING", "Training runtime is unavailable") from error
        if isinstance(actor, IntegratedSessionActor):
            if unavailable:
                receipt = self.store.accept(actor_id, command)
                if receipt.status != "QUEUED":
                    return receipt, None
                raise TrainingStoreError("SESSION_RECOVERING", "Training runtime is unavailable")
            return submit_workflow(self.store, actor, actor_id, command, checkpoint)
        receipt = self.store.accept(actor_id, command)
        if receipt.status != "QUEUED":
            return receipt, None
        if unavailable:
            raise TrainingStoreError("SESSION_RECOVERING", "Training runtime is unavailable")
        _, accepted = self.store.command_record(command.session_id, actor_id, command.idempotency_key)
        baseline = self.store.snapshot(command.session_id)
        staged = deepcopy(actor)
        status = LIFECYCLE[accepted.command_type]
        staged.is_paused = status != "RUNNING"
        staged.command_revision = baseline.session.aggregate_revision + 1
        snapshot = self._snapshot(staged, baseline, status)
        snapshot.session.aggregate_revision += 1
        event = self._event(snapshot, f"SESSION_{status}")
        snapshot.event_sequence += 1
        result = self.store.apply(accepted.command_id, snapshot, (event,),
                                  expected_snapshot_id=baseline.snapshot_id, runtime_state=checkpoint(staged))
        return result, staged if result.status == "APPLIED" else None

    async def step(self, session: LiveSession) -> None:
        await self._complete(self._step(session))

    async def _step(self, session: LiveSession) -> None:
        lock_started = self.store.trace.clock()
        async with session.lock:
            self.store.trace.record("tick.lock_wait", lock_started, self.store.trace.clock(),
                                    session_id=session.actor.session_id, session_epoch=session.actor.session_epoch,
                                    tick=session.actor.current_tick+1, running=not session.actor.is_paused)
            if session.failed or session.actor.is_paused or self.closed:
                return
            sample_ns = self.store.trace.clock()
            try:
                staged, baseline = await asyncio.to_thread(self._step_staged, session.actor, session.baseline, sample_ns)
            except Exception:
                session.failed = True
                session.progress.observe(self.store.trace, session.actor, running=False)
                raise
            if staged is not None:
                session.actor = staged
            session.baseline = baseline
            session.progress.observe(self.store.trace, session.actor, committed=baseline,
                                     sample_ns=sample_ns if staged is not None else None)

    def _step_staged(self, actor: SessionActor, baseline: FullSnapshot | None, enqueued_ns: int | None = None
                     ) -> tuple[SessionActor | None, FullSnapshot | None]:
        tags = {"session_id": actor.session_id, "session_epoch": actor.session_epoch, "tick": actor.current_tick + 1}
        if enqueued_ns is not None:
            self.store.trace.record("tick.worker_wait", enqueued_ns, self.store.trace.clock(), **tags)
        with self.store.trace.span("tick.compute", **tags):
            with self.store.trace.span("tick.stage", **tags):
                staged = deepcopy(actor)
            with self.store.trace.span("tick.actor", **tags):
                staged.tick()
        if staged.should_emit_snapshot():
            if baseline is None:
                baseline = self.store.snapshot(actor.session_id)
            if baseline.session.status != "RUNNING":
                return None, baseline
            with self.store.trace.span("snapshot.build", session_id=actor.session_id,
                                       session_epoch=actor.session_epoch, tick=staged.current_tick):
                snapshot = self._snapshot(staged, baseline, "RUNNING")
            try:
                if isinstance(staged, IntegratedSessionActor):
                    baseline = publish_workflow(self.store, staged, snapshot, checkpoint)
                else:
                    baseline = self.store.commit_snapshot(snapshot, expected_snapshot_id=baseline.snapshot_id,
                                                          runtime_state=checkpoint(staged))
            except TrainingStoreError as error:
                if error.code != "STALE_REVISION":
                    raise
                # Another command may have appended an outcome while this tick
                # was staged. Retry from the installed actor and latest cursor.
                return None, self.store.snapshot(actor.session_id)
        return staged, baseline

    async def _run(self, session: LiveSession) -> None:
        step_ns = round(SessionActor.STEP_S * 1e9)
        deadline = self.store.trace.clock() + step_ns
        try:
            while True:
                waiting = await wait_for_deadline(self.store.trace, deadline,
                    session_id=session.actor.session_id, session_epoch=session.actor.session_epoch,
                    tick=session.actor.current_tick+1, running=not session.actor.is_paused)
                if session.failed:
                    break
                start = self.store.trace.clock()
                lateness = max(0, start - deadline)
                running = not session.actor.is_paused
                try:
                    with self.store.trace.span("tick.boundary", session_id=session.actor.session_id,
                                               session_epoch=session.actor.session_epoch,
                                               tick=session.actor.current_tick,
                                               running=running):
                        await self.step(session)
                finally:
                    end = self.store.trace.clock()
                    self.store.trace.record("tick.schedule", start, start,
                                            session_id=session.actor.session_id, session_epoch=session.actor.session_epoch,
                                            tick=session.actor.current_tick, running=running, lateness_ms=lateness / 1e6,
                                            boundary_ms=(end-start)/1e6,
                                            missed_deadlines=max(0, (end-deadline) // step_ns), **waiting)
                deadline = max(deadline + step_ns, self.store.trace.clock())
        except asyncio.CancelledError:
            raise
        except Exception:
            session.failed = True
            logger.exception("Training runtime failed for %s", session.actor.session_id)
        # Failed actors stop immediately. Until the terminal record is durable,
        # reads report SESSION_RECOVERING instead of advertising a frozen run.
        delay = self.FAILURE_RETRY_S
        while not self.closed:
            try:
                await self._complete(self._record_failure(session))
                return
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.warning("Could not persist training failure for %s; retrying", session.actor.session_id,
                               exc_info=True)
                await asyncio.sleep(delay)
                delay = min(delay * 2, 5.0)

    async def _record_failure(self, session: LiveSession) -> None:
        async with session.lock:
            await asyncio.to_thread(self._commit_failure, session.actor.session_id)
            session.failure_committed = True
            session.progress.observe(self.store.trace, session.actor, running=False)
            self.sessions.pop(session.actor.session_id, None)

    def _commit_failure(self, session_id: str, *, reason: str = "ACTOR_FAILED") -> None:
        baseline = self.store.snapshot(session_id)
        if baseline.session.status == "FAILED":
            return
        snapshot_id = baseline.snapshot_id
        baseline.session.status = "FAILED"
        baseline.session.aggregate_revision += 1
        event = self._event(baseline, "SESSION_FAILED")
        event.data["reason"] = reason
        baseline.event_sequence += 1
        self.store.commit_snapshot(baseline, (event,), expected_snapshot_id=snapshot_id)

    async def close(self) -> None:
        self.closed = True
        tasks = list(self._tasks)
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        while self._operations:
            await asyncio.gather(*tuple(self._operations), return_exceptions=True)
        for session in self.sessions.values():
            session.progress.observe(self.store.trace, session.actor, running=False)

