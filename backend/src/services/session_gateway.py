"""
The gateway: live training sessions spread over worker processes, one core each.

Author: Lau Xiang Yang
Date Created: 18/11/2026
Last Modified: 18/11/2026

Description:
    Phase 2 of the multi-core sessions plan. Under CPython's global interpreter lock every session that
    ticks in the backend's process shares one core, so past a few sessions with traffic each one's
    clock falls behind the wall clock. GatewaySessionService keeps the backend's process as the gateway
    and runs the sessions in session worker processes (session_worker.py):

    - The gateway keeps every HTTP route and reply as it was, the session registry (session ID to
      worker), the only SQLite writer (TrainingStore and its lock file), each live session's latest
      committed snapshot JSON (the store's, set as each commit lands) and its latest traffic frames,
      which the workers push as they make them.
    - Each worker owns the actors of the sessions placed on it and ticks them as the in-process runtime
      does. Its publications are encoded there and committed here; commands, route approvals and the
      signal state are forwarded to it.

    Placement and admission go by measured load. Each worker reports, four times a second, each of its
    sessions' mean cost per tick; a session not yet measured (paused, or just placed) counts as the
    median of the measured sessions like it, or a default before there are any. A new session goes to
    the least loaded worker if that worker's load plus the new session's estimate stays within
    TICK_BUDGET_MS (60% of a 50 ms tick, the rest left for the commit round trip and garbage collection);
    an empty worker takes any one session. Otherwise creation is refused with QUEUE_FULL, as the fixed
    cap of sessions with traffic refused it before. MAX_SESSIONS stays the product limit.

    A worker that exits, whose pipe ends, or that stops reporting for HANG_TIMEOUT_S is ended, and its
    sessions are recovered from their last committed checkpoint onto the workers still running (or its
    replacement, if none is), through TrainingStore.recover_session: a new epoch, paused, queued
    commands marked for reconfirmation, as after a backend restart. Until then they answer
    SESSION_RECOVERING. Command outcomes are decided by the gateway's writer, so a command already
    applied is not applied again. A session no worker can take, whether its worker was lost or it was
    left running when the backend last stopped, answers SESSION_RECOVERING and is tried again, waiting
    longer each time; one still not restored after RECOVERY_GIVE_UP_S is committed FAILED, releasing
    its slot.

    A start that fails part way (a worker that cannot start, or a restore that raises) is undone before
    the error reaches the caller: no worker, monitor or recovery is left running, and no session is
    kept. A start that failed for want of workers fails the same way for every caller after it, so the
    route that falls back to the in-process runtime (training_v2) never has two runtimes ticking one
    session.

    F9_SESSION_WORKERS sets the number of workers: "auto" (the default) is the physical cores less two,
    for Unity's main and render threads and this process, within 1..8; 0 runs every session in this
    process (TrainingSessionService), as tests and debugging do.
"""

from __future__ import annotations

import asyncio
from collections import deque
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
import logging
import multiprocessing
import os
from pathlib import Path
import statistics
import sys
from typing import Any

from contracts.v2.models import CommandStatus, DispatcherCommand, FullSnapshot
from contracts.v2.sessions import CreateSessionRequest
from src.core.training_telemetry import boundary
from src.persistence.store_client import serve_store_call
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.services import session_worker
from src.services.integrated_actor import SCENARIO_SELECTOR, IntegratedSessionActor, get_workflow_definition
from src.services.assessment import SCORED_SCENARIO
from src.services.session_actor import SessionActor
from src.services.session_ipc import Channel, ChannelClosed
from src.services.training_sessions import (LIFECYCLE, TRAFFIC_FRAMES, FrameFeed, SessionServiceBase,
                                            TrainingSessionService, commit_failure, freeze_static_heap)

logger = logging.getLogger(__name__)
WORKERS_ENV = "F9_SESSION_WORKERS"
MAX_AUTO_WORKERS = 8
STEP_MS = SessionActor.STEP_S * 1000
# Defaults for a session's cost per tick until sessions like it have been measured: the slow laptop's
# Phase 0 baseline (docs/session_creation.md), so a new machine admits too few rather than too many
TRAFFIC_COST_MS = 15.0
SESSION_COST_MS = 5.4
# The same for a session with traffic when sessions drive the native traffic engine
# (src.traffic.native_traffic): 5-6.4 ms measured on that laptop with 8-16 sessions, rounded up
NATIVE_TRAFFIC_COST_MS = 7.0
# Ticks a session must have run before its own measured cost replaces the estimate
MIN_COST_SAMPLES = 20
# A worker loads the scenario and road network before it is ready
START_TIMEOUT_S = 180.0
# A worker reports four times a second; this long without a word, it is taken as stuck
HANG_TIMEOUT_S = 3.0
MONITOR_S = 0.5
REQUEST_TIMEOUT_S = 30.0
# Creating or restoring an integrated session starts its traffic, which takes a while on a slow laptop
PLACE_TIMEOUT_S = 120.0
SHUTDOWN_TIMEOUT_S = 15.0
RESTORE_ATTEMPTS = 3
# A lost worker's session that no worker could restore is tried again after this long, doubling up to
# RECOVERY_RETRY_MAX_S; past RECOVERY_GIVE_UP_S it is committed FAILED, so it no longer holds a slot
RECOVERY_RETRY_S = 1.0
RECOVERY_RETRY_MAX_S = 30.0
RECOVERY_GIVE_UP_S = 300.0
# The worker's StoreClient writes other than publications, run on a small pool here
STORE_CALLS = frozenset({"next_session_number", "create_session", "accept", "apply"})


class WorkerStartError(RuntimeError):
    """A session worker process could not be started; the backend can run its sessions in-process."""


def physical_cores() -> int | None:
    """The machine's physical cores, where the platform tells, else None."""
    if os.name == "nt":
        try:
            import ctypes
            from ctypes import wintypes
            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            length = wintypes.DWORD(0)
            # RelationProcessorCore: one record a physical core
            kernel32.GetLogicalProcessorInformationEx(0, None, ctypes.byref(length))
            buffer = ctypes.create_string_buffer(length.value)
            if not length.value or not kernel32.GetLogicalProcessorInformationEx(0, buffer, ctypes.byref(length)):
                return None
            cores, offset = 0, 0
            while offset < length.value:
                size = int.from_bytes(buffer.raw[offset + 4:offset + 8], "little")
                if size <= 0:
                    break
                cores, offset = cores + 1, offset + size
            return cores or None
        except (OSError, AttributeError):
            return None
    try:
        cores, physical = set(), None
        for line in Path("/proc/cpuinfo").read_text().splitlines():
            key, _, value = line.partition(":")
            key = key.strip()
            if key == "physical id":
                physical = value.strip()
            elif key == "core id":
                cores.add((physical, value.strip()))
        return len(cores) or None
    except OSError:
        return None


def configured_workers(value: str | None = None) -> int:
    """F9_SESSION_WORKERS as a number of worker processes; "auto" when unset."""
    value = (os.environ.get(WORKERS_ENV, "auto") if value is None else value).strip().lower()
    if value in {"", "auto"}:
        cores = physical_cores() or max(1, (os.cpu_count() or 2) // 2)
        return max(1, min(MAX_AUTO_WORKERS, cores - 2))
    try:
        count = int(value)
    except ValueError:
        raise ValueError(f"{WORKERS_ENV} must be auto or a whole number of workers, not {value!r}") from None
    if not 0 <= count <= 64:
        raise ValueError(f"{WORKERS_ENV} must be between 0 and 64")
    return count


def create_session_service(store: TrainingStore, *, retention_keep_latest: int | None = None,
                           workers: int | None = None) -> SessionServiceBase:
    """The session service F9_SESSION_WORKERS (or workers) asks for: the gateway over worker processes,
    or every session in this process with 0."""
    count = configured_workers() if workers is None else workers
    if count and multiprocessing.current_process().daemon:
        # A daemonic process may not start processes of its own
        logger.warning("Running training sessions in this process: it is daemonic and cannot start workers")
        count = 0
    if count == 0:
        return TrainingSessionService(store, retention_keep_latest=retention_keep_latest)
    return GatewaySessionService(store, workers=count, retention_keep_latest=retention_keep_latest)


@dataclass(eq=False)
class WorkerHandle:
    index: int
    process: Any
    ready: asyncio.Future
    channel: Channel | None = None
    pid: int | None = None
    started: bool = False
    # Set once it is found gone or stuck: its sessions are being recovered elsewhere, and nothing it
    # still sends is taken
    lost: bool = False
    # Set while the gateway shuts it down: it still commits what it has under way, but takes no sessions
    stopping: bool = False
    last_seen: float = 0.0
    max_lateness_ms: float = 0.0
    missed_deadlines: int = 0

    @property
    def available(self) -> bool:
        return self.started and not self.lost and not self.stopping


@dataclass(eq=False)
class GatewaySession(FrameFeed):
    """What the gateway keeps of a live session that runs on a worker. worker is None for a session
    not yet restored on any (recovering)."""
    session_id: str
    worker: WorkerHandle | None
    integrated: bool
    traffic: bool
    tick: int | None = None
    running: bool = False
    failed: bool = False
    # Its worker was lost: until it is restored on another, it answers SESSION_RECOVERING
    recovering: bool = False
    cost_ms: float = 0.0
    cost_samples: int = 0
    frames: deque = field(default_factory=lambda: deque(maxlen=TRAFFIC_FRAMES))
    frame_signal: asyncio.Event = field(default_factory=asyncio.Event)


class GatewaySessionService(SessionServiceBase):
    """The session service's interface, with every live session ticking in a worker process."""
    # A worker admits another session while its load plus the new one's estimate stays within this
    TICK_BUDGET_MS = 0.6 * STEP_MS

    def __init__(self, store: TrainingStore, *, workers: int, retention_keep_latest: int | None = None,
                 manual_clock: bool = False):
        if workers < 1:
            raise ValueError("The gateway needs at least one worker; use TrainingSessionService for none")
        super().__init__(store, retention_keep_latest=retention_keep_latest)
        self.worker_count = workers
        self.manual_clock = manual_clock
        self.workers: list[WorkerHandle | None] = [None] * workers
        self.sessions: dict[str, GatewaySession] = {}
        self._loop: asyncio.AbstractEventLoop | None = None
        self._executor = ThreadPoolExecutor(max_workers=4, thread_name_prefix="gateway-store")
        self._context = multiprocessing.get_context("spawn")
        self._settings: dict | None = None
        self._monitor: asyncio.Task | None = None
        self._background: set[asyncio.Task] = set()
        self._replacing: dict[int, asyncio.Task] = {}
        # Lost workers' sessions being recovered (_recover), each its own task
        self._recoveries: set[asyncio.Task] = set()
        # Why the workers could not start: every later start fails the same way at once
        self._start_error: WorkerStartError | None = None

    # ---------------------------------------------------------------------------------------------
    # Workers
    # ---------------------------------------------------------------------------------------------

    def _worker_config(self, index: int) -> dict:
        log = getattr(sys.stderr, "name", None)
        return {"index": index, "parent_pid": os.getpid(), "database_path": str(self.store.path),
                "settings": self._settings, "trace_capacity": self.store.trace.capacity,
                "validate_publications": self.store.validate_publications, "manual_clock": self.manual_clock,
                # Output the gateway writes to a file (Tools/bundled_backend.py) goes beside it
                "log_path": log if isinstance(log, str) and not log.startswith("<") else None}

    async def _spawn(self, index: int) -> WorkerHandle:
        """Starts worker index and waits until it has loaded the static data and is ready."""
        parent, child = self._context.Pipe(duplex=True)
        process = self._context.Process(target=session_worker.main, args=(child, self._worker_config(index)),
                                        name=f"f9-session-worker-{index}", daemon=True)
        handle = WorkerHandle(index, process, self._loop.create_future())
        try:
            await asyncio.to_thread(process.start)
        except Exception as error:
            parent.close()
            raise WorkerStartError(f"Could not start session worker {index}: {error}") from error
        finally:
            child.close()
        handle.channel = Channel(parent, name=f"gateway-worker-{index}",
                                 on_message=lambda kind, request_id, payload: self._on_message(handle, kind,
                                                                                               request_id, payload),
                                 on_closed=lambda: self._call_soon(self._lost_soon, handle, "closed its pipe"))
        try:
            ready = await asyncio.wait_for(asyncio.shield(handle.ready), START_TIMEOUT_S)
        except (Exception, asyncio.CancelledError) as error:
            handle.lost = True
            if not handle.ready.done():
                handle.ready.cancel()
            await asyncio.to_thread(self._stop_process, handle, kill=True)
            if isinstance(error, asyncio.CancelledError):
                raise
            raise WorkerStartError(f"Session worker {index} did not start: {error!r}") from error
        handle.pid, handle.started, handle.last_seen = ready["pid"], True, self._loop.time()
        logger.info("Session worker %d ready (pid %d)", index, handle.pid)
        return handle

    @staticmethod
    def _stop_process(handle: WorkerHandle, *, kill: bool = False, timeout: float = SHUTDOWN_TIMEOUT_S) -> None:
        """Ends the worker's process (at once with kill) and its channel. Runs on a thread."""
        process = handle.process
        if kill and process.is_alive():
            process.kill()
        process.join(timeout)
        if process.is_alive():
            logger.warning("Session worker %d did not stop; ending it", handle.index)
            process.kill()
            process.join(5)
        if handle.channel is not None:
            handle.channel.close()
            handle.channel.join(5)

    def _call_soon(self, callback, *args) -> None:
        """callback(*args) on the event loop, from a channel's reader thread."""
        try:
            self._loop.call_soon_threadsafe(callback, *args)
        except RuntimeError:
            pass  # The event loop has closed: the gateway is gone

    def _ready_workers(self) -> list[WorkerHandle]:
        return [handle for handle in self.workers if handle is not None and handle.available]

    def _on_message(self, handle: WorkerHandle, kind: str, request_id: int, payload: Any) -> None:
        """A message from a worker, on its channel's reader thread: store writes go to the writer (a
        publication without any thread of ours waiting on it), anything else to the event loop."""
        if kind == "commit" or kind in STORE_CALLS:
            if handle.lost:
                handle.channel.reply(request_id, error=ChannelClosed("Session worker was replaced"))
                return
            try:
                if kind == "commit":
                    future = self.store.submit_commit(payload)
                else:
                    future = self._executor.submit(serve_store_call, self.store, kind, payload)
            except Exception as error:
                handle.channel.reply(request_id, error=error)
                return
            handle.channel.answer(request_id, future)
            return
        self._call_soon(self._on_event, handle, kind, payload)

    def _owned(self, handle: WorkerHandle, session_id: str) -> GatewaySession | None:
        session = self.sessions.get(session_id)
        return session if session is not None and session.worker is handle else None

    def _on_event(self, handle: WorkerHandle, kind: str, payload: Any) -> None:
        if kind == "ready":
            if not handle.ready.done():
                handle.ready.set_result(payload)
            return
        if handle.lost:
            return
        handle.last_seen = self._loop.time()
        if kind == "frame":
            session_id, tick, frame = payload
            session = self._owned(handle, session_id)
            if session is not None:
                session.tick = tick
                session.add_frame(tick, frame)
        elif kind == "progress":
            handle.max_lateness_ms = payload["max_lateness_ms"]
            handle.missed_deadlines += payload["missed_deadlines"]
            for session_id, progress in payload["sessions"].items():
                session = self._owned(handle, session_id)
                if session is not None:
                    session.tick, session.running = progress["tick"], progress["running"]
                    session.failed = session.failed or progress["failed"]
                    session.cost_ms, session.cost_samples = progress["cost_ms"], progress["samples"]
        elif kind == "state":
            session = self._owned(handle, payload["session_id"])
            if session is not None:
                if payload.get("removed"):
                    self._remove(session)
                elif payload.get("failed"):
                    session.failed = True

    def _lost_soon(self, handle: WorkerHandle, reason: str) -> None:
        if not handle.ready.done():
            handle.ready.set_exception(WorkerStartError(f"Session worker {handle.index} {reason} while starting"))
            return
        if handle.lost or handle.stopping or self.closed:
            return
        handle.lost = True
        self._background_task(self._worker_lost(handle, reason))

    def _background_task(self, coroutine) -> asyncio.Task:
        task = asyncio.create_task(coroutine)
        self._background.add(task)
        task.add_done_callback(self._background_done)
        return task

    def _background_done(self, task: asyncio.Task) -> None:
        self._background.discard(task)
        if not task.cancelled() and task.exception() is not None:
            logger.error("Session worker supervision failed", exc_info=task.exception())

    async def _watch_workers(self) -> None:
        previous = self._loop.time()
        while not self.closed:
            await asyncio.sleep(MONITOR_S)
            now = self._loop.time()
            if now - previous > MONITOR_S + HANG_TIMEOUT_S / 2:
                # This process was itself stalled (or the machine slept): the workers' silence says
                # nothing about them, so each gets a fresh hearing
                for handle in self._ready_workers():
                    handle.last_seen = now
            previous = now
            for handle in self._ready_workers():
                if not handle.process.is_alive():
                    self._lost_soon(handle, f"exited (code {handle.process.exitcode})")
                elif now - handle.last_seen > HANG_TIMEOUT_S:
                    self._lost_soon(handle, f"stopped answering for {now - handle.last_seen:.1f} s")

    async def _worker_lost(self, handle: WorkerHandle, reason: str) -> None:
        lost = [session for session in self.sessions.values() if session.worker is handle]
        logger.error("Session worker %d (pid %s) %s; recovering its %d sessions", handle.index, handle.pid,
                     reason, len(lost))
        now = self.store.trace.clock()
        self.store.trace.record("worker.lost", now, now, worker=handle.index, worker_pid=handle.pid,
                                session_count=len(lost), outcome="ERROR", error_code="WORKER_LOST")
        # Requests waiting on it fail at once (ChannelClosed: SESSION_RECOVERING), and so do its sessions
        handle.channel.close()
        for session in lost:
            session.recovering = True
            session.frames.clear()
            session.wake_frame_waiters()
        await asyncio.to_thread(self._stop_process, handle, kill=True)
        if self.closed:
            return
        self.workers[handle.index] = None
        replacement = self._replacing.get(handle.index)
        if replacement is None or replacement.done():
            self._replacing[handle.index] = replacement = self._background_task(self._replace(handle.index))
        if not self._ready_workers():
            # The only worker: its sessions wait for the replacement
            await asyncio.wait({replacement})
        targets = self._plan([session.traffic for session in lost])
        await asyncio.gather(*(self._complete(self._recover(session, target))
                               for session, target in zip(lost, targets)))

    async def _replace(self, index: int) -> None:
        delay = 1.0
        while not self.closed:
            try:
                handle = await self._spawn(index)
            except WorkerStartError:
                logger.exception("Could not replace session worker %d; trying again in %.0f s", index, delay)
                await asyncio.sleep(delay)
                delay = min(delay * 2, 30.0)
                continue
            if self.closed:
                handle.stopping = True
                await self._shutdown_worker(handle)
                return
            self.workers[index] = handle
            return

    async def _recover(self, session: GatewaySession, target: WorkerHandle | None = None) -> None:
        """A session whose worker was lost: back to its last committed checkpoint, on another worker.
        While no worker can take it, it is tried again, each wait longer (RECOVERY_RETRY_S doubling up
        to RECOVERY_RETRY_MAX_S); one still not restored after RECOVERY_GIVE_UP_S is committed FAILED,
        so it no longer answers SESSION_RECOVERING, holds a slot or blocks its deletion."""
        task = asyncio.current_task()
        self._recoveries.add(task)
        try:
            with self.store.trace.span("runtime.session_recovery", session_id=session.session_id,
                                       reason="WORKER_LOST") as fields:
                deadline = self._loop.time() + RECOVERY_GIVE_UP_S
                delay, attempts = RECOVERY_RETRY_S, 0
                while True:
                    attempts += 1
                    try:
                        # Each attempt starts a new epoch, so nothing an earlier attempt's worker might
                        # still commit is taken
                        await asyncio.to_thread(self.store.recover_session, session.session_id)
                    except TrainingStoreError as error:
                        if error.code != "CORRUPT_CHECKPOINT":
                            raise
                        logger.exception("Cannot recover training runtime for %s", session.session_id)
                        await asyncio.to_thread(commit_failure, self.store, session.session_id,
                                                reason="CORRUPT_CHECKPOINT")
                        self._remove(session)
                        fields.update(outcome="FAILED", attempts=attempts)
                        return
                    if await self._restore(session.session_id, session.traffic, target):
                        fields.update(outcome="RESTORED", attempts=attempts)
                        return
                    if self.closed or self.sessions.get(session.session_id) is not session:
                        # Shutting down, or the session was settled some other way
                        fields.update(outcome="UNAVAILABLE", attempts=attempts)
                        return
                    if self._loop.time() + delay > deadline:
                        break
                    logger.warning("No session worker could restore %s; trying again in %.0f s",
                                   session.session_id, delay)
                    await asyncio.sleep(delay)
                    delay, target = min(delay * 2, RECOVERY_RETRY_MAX_S), None
                logger.error("Training session %s was not restored on any worker in %.0f s; marking it failed",
                             session.session_id, RECOVERY_GIVE_UP_S)
                await asyncio.to_thread(commit_failure, self.store, session.session_id, reason="RECOVERY_FAILED")
                self._remove(session)
                fields.update(outcome="FAILED", attempts=attempts)
        finally:
            self._recoveries.discard(task)

    async def _restore(self, session_id: str, traffic: bool, handle: WorkerHandle | None = None) -> bool:
        """Restores a session from its last committed checkpoint on handle, or the least loaded worker.
        False if no worker could take it (it stays unavailable)."""
        for attempt in range(RESTORE_ATTEMPTS):
            target = handle if handle is not None and handle.available else self._least_loaded()
            if target is None:
                await asyncio.sleep(1.0)
                continue
            try:
                reply = await self._request(target, "restore", {"session_id": session_id}, PLACE_TIMEOUT_S)
            except TrainingStoreError as error:
                if error.code != "SESSION_RECOVERING":
                    raise
                if not isinstance(error, ChannelClosed):
                    # Stuck: it might still restore the session later, so it is ended, not merely skipped
                    self._lost_soon(target, "did not answer a restore in time")
                handle = None
                await asyncio.sleep(0.5)
                continue
            if self.closed or target.stopping:
                # Restored on a worker being shut down (the gateway closing, or its start undone): that
                # worker will not run it, and the next start restores it from the store
                return False
            if reply.get("failed"):
                # Committed FAILED by the worker: its slot is released
                previous = self.sessions.pop(session_id, None)
                if previous is not None:
                    previous.wake_frame_waiters()
                return True
            self._register(target, reply)
            return True
        logger.error("Could not restore training session %s on any worker", session_id)
        return False

    def _register(self, handle: WorkerHandle, reply: dict) -> GatewaySession:
        session = GatewaySession(reply["session_id"], handle, integrated=reply["integrated"], traffic=reply["traffic"],
                                 tick=reply["tick"], running=reply["running"])
        if reply["frame"] is not None:
            session.frames.append(tuple(reply["frame"]))
        previous = self.sessions.get(session.session_id)
        self.sessions[session.session_id] = session
        if previous is not None:
            # A 3D view waiting on the old record asks the new one
            previous.wake_frame_waiters()
        return session

    def _remove(self, session: GatewaySession) -> None:
        if self.sessions.get(session.session_id) is session:
            del self.sessions[session.session_id]
        session.wake_frame_waiters()

    async def _request(self, handle: WorkerHandle | None, kind: str, payload: Any, timeout: float) -> Any:
        if handle is None or handle.lost or handle.channel is None:
            raise ChannelClosed("Session worker was replaced")
        future = handle.channel.request(kind, payload)
        try:
            return await asyncio.wait_for(asyncio.wrap_future(future), timeout)
        except (TimeoutError, asyncio.TimeoutError):
            raise TrainingStoreError("SESSION_RECOVERING", "Session worker did not answer in time") from None

    async def _shutdown_worker(self, handle: WorkerHandle) -> None:
        try:
            await asyncio.wait_for(asyncio.wrap_future(handle.channel.request("shutdown")), SHUTDOWN_TIMEOUT_S)
        except Exception:
            logger.warning("Session worker %d did not shut down cleanly", handle.index, exc_info=True)
        await asyncio.to_thread(self._stop_process, handle)

    # ---------------------------------------------------------------------------------------------
    # Load
    # ---------------------------------------------------------------------------------------------

    def _measured(self, session: GatewaySession) -> bool:
        return session.running and not session.recovering and session.cost_samples >= MIN_COST_SAMPLES

    def estimate_ms(self, traffic: bool) -> float:
        """A session's expected cost per tick: the median measured of the running sessions like it."""
        measured = [s.cost_ms for s in self.sessions.values() if s.traffic == traffic and self._measured(s)]
        if measured:
            return statistics.median(measured)
        if not traffic:
            return SESSION_COST_MS
        from src.traffic import native_traffic
        try:
            native = native_traffic.engine() is native_traffic.NativeCityTraffic
        except (RuntimeError, ValueError):
            native = False
        return NATIVE_TRAFFIC_COST_MS if native else TRAFFIC_COST_MS

    def _estimator(self):
        """estimate_ms for one decision, each kind worked out once: the sessions do not change during it."""
        estimates: dict[bool, float] = {}

        def estimate(traffic: bool) -> float:
            if traffic not in estimates:
                estimates[traffic] = self.estimate_ms(traffic)
            return estimates[traffic]
        return estimate

    def _loads(self, estimate=None) -> dict[WorkerHandle, float]:
        """Each ready worker's load_ms, in one pass over the sessions."""
        estimate = estimate or self._estimator()
        loads = {handle: 0.0 for handle in self._ready_workers()}
        for session in self.sessions.values():
            if session.worker in loads:
                loads[session.worker] += session.cost_ms if self._measured(session) else estimate(session.traffic)
        return loads

    def load_ms(self, handle: WorkerHandle) -> float:
        """The tick time a worker's sessions take, or will once running, in milliseconds a tick."""
        estimate = self._estimator()
        return sum(session.cost_ms if self._measured(session) else estimate(session.traffic)
                   for session in self.sessions.values() if session.worker is handle)

    def _plan(self, traffic: list[bool]) -> list[WorkerHandle | None]:
        """Workers for sessions about to be restored together, one each, spread by estimated load,
        heaviest first, before any of them is measured."""
        estimate = self._estimator()
        planned = self._loads(estimate)
        targets: list[WorkerHandle | None] = [None] * len(traffic)
        if not planned:
            return targets
        for index in sorted(range(len(traffic)), key=lambda i: not traffic[i]):
            handle = min(planned, key=planned.get)
            planned[handle] += estimate(traffic[index])
            targets[index] = handle
        return targets

    def _least_loaded(self) -> WorkerHandle | None:
        loads = self._loads()
        return min(loads, key=loads.get) if loads else None

    def _admit_worker(self, traffic: bool) -> WorkerHandle:
        estimate = self._estimator()
        loads = self._loads(estimate)
        if not loads:
            raise TrainingStoreError("QUEUE_FULL", "No session worker is running; try again shortly")
        handle = min(loads, key=loads.get)
        empty = not any(session.worker is handle for session in self.sessions.values())
        if not empty and loads[handle] + estimate(traffic) > self.TICK_BUDGET_MS:
            raise TrainingStoreError("QUEUE_FULL", "Training capacity reached: every session worker is busy; "
                                                   "archive a session to start another")
        return handle

    # ---------------------------------------------------------------------------------------------
    # The session service's interface
    # ---------------------------------------------------------------------------------------------

    async def _initialize(self) -> None:
        async with self._initialize_lock:
            if self._initialized:
                return
            if self._start_error is not None:
                # The workers could not start: callers that waited on this lock fail as the first did,
                # rather than start workers beside the in-process runtime that replaces this gateway
                raise self._start_error
            if self.closed:
                raise TrainingStoreError("SESSION_RECOVERING", "Training runtime is shutting down")
            self._loop = asyncio.get_running_loop()
            # The gateway still validates previews and serves the static scenario, and its garbage
            # collector gains from the freeze as a worker's does
            await asyncio.to_thread(freeze_static_heap)
            self._settings = session_worker.runtime_settings()
            try:
                spawned = await asyncio.gather(*(self._spawn(index) for index in range(self.worker_count)),
                                               return_exceptions=True)
                self.workers = [handle if isinstance(handle, WorkerHandle) else None for handle in spawned]
                failure = next((error for error in spawned if isinstance(error, BaseException)), None)
                if failure is not None:
                    raise failure
                self._monitor = asyncio.create_task(self._watch_workers())
                with self.store.trace.span("runtime.recovery") as fields:
                    await self._restore_all()
                    fields.update(restored_sessions=len(self.sessions), workers=self.worker_count)
            except BaseException as error:
                await self._abandon_start()
                if isinstance(error, WorkerStartError):
                    self._start_error = error
                raise
            self._initialized = True
            await self._retain()

    async def _abandon_start(self) -> None:
        """Undoes a start that failed part way: no monitor, replacement or recovery left running, no
        worker process and no session kept, so the next start, or the in-process runtime that takes
        over, restores every session from the store alone."""
        tasks = [*self._background, *self._replacing.values(), *self._recoveries]
        if self._monitor is not None:
            tasks.append(self._monitor)
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        self._monitor = None
        self._replacing.clear()
        # A worker found lost before its supervision was cancelled may not have been ended yet
        lost = [handle for handle in self.workers if handle is not None and handle.lost]
        await asyncio.gather(*(asyncio.to_thread(self._stop_process, handle, kill=True) for handle in lost))
        await self._shutdown_workers()
        self.workers = [None] * self.worker_count
        sessions, self.sessions = self.sessions, {}
        for session in sessions.values():
            session.wake_frame_waiters()

    async def _restore_all(self) -> None:
        session_ids = await asyncio.to_thread(self.store.runtime_ids)
        traffic_ids = await asyncio.to_thread(self.store.traffic_runtime_ids)
        traffic = [session_id in traffic_ids for session_id in session_ids]
        targets = self._plan(traffic)
        restored = await asyncio.gather(*(self._restore(session_id, kind, target)
                                          for session_id, kind, target in zip(session_ids, traffic, targets)))
        for session_id, kind, done in zip(session_ids, traffic, restored):
            if not done and session_id not in self.sessions:
                # No worker took it now: it is kept, recovering, and tried again in the background as a
                # lost worker's session is, rather than left out (no runtime, yet its commands accepted)
                logger.warning("Training session %s was not restored at startup; trying again", session_id)
                integrated = await asyncio.to_thread(self._integrated, session_id)
                session = self.sessions[session_id] = GatewaySession(session_id, None, integrated=integrated,
                                                                     traffic=kind, recovering=True)
                self._background_task(self._recover(session))
        if len(self.sessions) > self.MAX_SESSIONS:
            raise TrainingStoreError("QUEUE_FULL", "Too many recoverable training sessions")

    def _integrated(self, session_id: str) -> bool:
        """Whether a session's checkpoint is of an integrated actor, as TrainingSessionService decides
        when it restores one. False for one that cannot be read: its recovery then commits it FAILED."""
        try:
            snapshot, state = self.store.runtime(session_id)
            return state["version"] in (3, 4) and snapshot.spatial is None
        except (TrainingStoreError, KeyError, TypeError, ValueError):
            return False

    async def _create(self, request: CreateSessionRequest, actor_id: str, key: str) -> FullSnapshot:
        async with self._creation_lock:
            digest = self._creation_digest(request)
            prior = await asyncio.to_thread(self.store.creation, actor_id, key, digest)
            if prior is not None:
                return prior
            self._check_scenario(request)
            if self.closed or len(self.sessions) >= self.MAX_SESSIONS:
                raise TrainingStoreError("QUEUE_FULL", "Training session capacity reached")
            traffic = request.scenario_revision_id in {SCENARIO_SELECTOR, SCORED_SCENARIO} and IntegratedSessionActor.TRAFFIC
            handle = self._admit_worker(traffic)
            payload = {"request": request.model_dump_json(), "actor_id": actor_id, "key": key, "digest": digest}
            with self.store.trace.span("session.place", worker=handle.index, traffic=traffic) as fields:
                try:
                    reply = await self._request(handle, "create", payload, PLACE_TIMEOUT_S)
                except TrainingStoreError as error:
                    if error.code != "SESSION_RECOVERING":
                        raise
                    # The worker was lost, or is stuck, mid-creation. A session it committed is restored
                    # elsewhere, so a retry with the same key finds it live
                    if not isinstance(error, ChannelClosed):
                        self._lost_soon(handle, "did not answer a creation in time")
                    created = await asyncio.to_thread(self.store.creation, actor_id, key, digest)
                    if created is None or not await self._complete(self._restore_created(created.session_id, traffic)):
                        raise
                    return await asyncio.to_thread(self.store.snapshot, created.session_id)
                fields.update(session_id=reply["session_id"])
            self._register(handle, reply)
            return FullSnapshot.model_validate_json(reply["snapshot"])

    async def _restore_created(self, session_id: str, traffic: bool) -> bool:
        if session_id in self.sessions:
            return True
        await asyncio.to_thread(self.store.recover_session, session_id)
        return await self._restore(session_id, traffic)

    def traffic_sessions(self) -> int:
        return sum(session.traffic for session in self.sessions.values())

    def live_ticks(self) -> dict[str, int | None]:
        return {session_id: None if self._unavailable(session) else session.tick
                for session_id, session in self.sessions.items()}

    def _unavailable(self, session: GatewaySession) -> bool:
        return session.recovering or session.failed

    def _traffic_session(self, session_id: str) -> GatewaySession:
        session = self.sessions.get(session_id)
        if session is None or session.failed or not session.integrated:
            raise TrainingStoreError("INVALID_REFERENCE", "No live traffic for this training session")
        if not session.traffic:
            raise TrainingStoreError("INVALID_REFERENCE", "This training session has no traffic")
        return session

    async def signal_state(self, session_id: str) -> dict:
        session = self.sessions.get(session_id)
        if session is None or session.failed or not session.integrated:
            raise TrainingStoreError("INVALID_REFERENCE", "No live signals for this training session")
        self._check_available(session_id)
        return await self._request(session.worker, "signal_state", {"session_id": session_id}, REQUEST_TIMEOUT_S)

    def _replay_receipt(self, actor_id: str, command: DispatcherCommand) -> CommandStatus:
        """For a session whose runtime is unavailable: a committed outcome is still given, but nothing new
        is journalled against a runtime that cannot apply it (as TrainingSessionService._submit_staged)."""
        try:
            self.store.command_record(command.session_id, actor_id, command.idempotency_key)
        except TrainingStoreError as error:
            if error.code != "INVALID_REFERENCE":
                raise
            raise TrainingStoreError("SESSION_RECOVERING", "Training runtime is unavailable") from error
        receipt = self.store.accept(actor_id, command)
        if receipt.status != "QUEUED":
            return receipt
        raise TrainingStoreError("SESSION_RECOVERING", "Training runtime is unavailable")

    async def submit(self, actor_id: str, command: DispatcherCommand) -> CommandStatus:
        await self.initialize()
        with self.store.trace.span("command.forward", command_id=command.command_id,
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
        if command.command_type not in LIFECYCLE and not session.integrated:
            raise TrainingStoreError("INVALID_TRANSITION", "Create a new attempt to reset this scenario")
        if self._unavailable(session) or self.closed:
            return await asyncio.to_thread(self._replay_receipt, actor_id, command)
        payload = {"actor_id": actor_id, "command": command.model_dump_json()}
        try:
            reply = await self._request(session.worker, "submit", payload, REQUEST_TIMEOUT_S)
        except ChannelClosed:
            return await asyncio.to_thread(self._replay_receipt, actor_id, command)
        if reply["removed"]:
            self._remove(session)
        return CommandStatus.model_validate_json(reply["status"])

    async def approve_route(self, actor_id: str, session_id: str, request) -> CommandStatus:
        await self.initialize()
        with self.store.trace.span("command.forward", command_id=request.command_id,
                                   command_type="APPROVE_ROUTE", session_id=session_id) as fields:
            result = await self._complete(self._approve_route(actor_id, session_id, request))
            fields.update(**boundary(result), command_id=result.command_id, outcome=result.status,
                          failure_code=result.failure_code)
            return result

    async def _approve_route(self, actor_id: str, session_id: str, request) -> CommandStatus:
        session = self.sessions.get(session_id)
        if session is None or not session.integrated:
            raise TrainingStoreError("UNKNOWN_CONSTRAINT", "Live actor has no admitted route-edit fixture")
        self._check_available(session_id)
        if self.closed:
            raise TrainingStoreError("SESSION_RECOVERING", "Training runtime is unavailable")
        # Route previews are kept in the process that made them, this one (the route-previews endpoint):
        # the worker that approves is handed the one approved
        retained = get_workflow_definition().routes.retained(request.preview_id)
        payload = {"actor_id": actor_id, "session_id": session_id, "request": request.model_dump_json(),
                   "preview": None if retained is None else tuple(item.model_dump_json() for item in retained)}
        reply = await self._request(session.worker, "approve_route", payload, REQUEST_TIMEOUT_S)
        if reply["removed"]:
            self._remove(session)
        return CommandStatus.model_validate_json(reply["status"])

    async def step(self, session_id: str, ticks: int = 1) -> int:
        """Diagnostic, with manual_clock: advances a session by ticks on its worker; returns its tick."""
        session = self.sessions[session_id]
        session.tick = await self._request(session.worker, "step", {"session_id": session_id, "ticks": ticks},
                                           PLACE_TIMEOUT_S)
        return session.tick

    async def worker_status(self) -> list[dict]:
        """Each running worker's index, process ID, CPU seconds used, memory and sessions."""
        loads = self._loads()

        async def status(handle: WorkerHandle) -> dict:
            return {"index": handle.index, "load_ms": loads.get(handle, 0.0),
                    "max_lateness_ms": handle.max_lateness_ms, "missed_deadlines": handle.missed_deadlines,
                    **await self._request(handle, "status", None, REQUEST_TIMEOUT_S)}
        return list(await asyncio.gather(*(status(handle) for handle in self._ready_workers())))

    async def export_trace(self) -> dict:
        """The gateway's trace and every worker's, merged: each record tagged with its process's ID and,
        for a worker's, its index. perf_counter_ns is one clock for the whole machine on Windows and
        Linux, so records from different processes line up."""
        exports = [(None, os.getpid(), self.store.trace.export())]
        for handle in self._ready_workers():
            exports.append((handle.index, handle.pid, await self._request(handle, "trace", None, PLACE_TIMEOUT_S)))
        merged = {"run_id": exports[0][2]["run_id"], "clock": "perf_counter_ns", "capacity": 0, "total_records": 0,
                  "dropped_records": 0, "counts": {}, "high_water": {}, "records": [], "processes": []}
        for index, pid, exported in exports:
            merged["capacity"] += exported["capacity"]
            merged["total_records"] += exported["total_records"]
            merged["dropped_records"] += exported["dropped_records"]
            for name, count in exported["counts"].items():
                merged["counts"][name] = merged["counts"].get(name, 0) + count
            for name, value in exported["high_water"].items():
                merged["high_water"][name] = max(merged["high_water"].get(name, value), value)
            tags = {"process_id": pid} if index is None else {"process_id": pid, "worker": index}
            merged["records"].extend({**record, **tags} for record in exported["records"])
            merged["processes"].append({"worker": index, "pid": pid, "run_id": exported["run_id"]})
        merged["records"].sort(key=lambda record: record["start_ns"])
        return merged

    async def _shutdown_workers(self) -> None:
        handles = [handle for handle in self.workers if handle is not None and handle.started and not handle.lost]
        for handle in handles:
            handle.stopping = True
        await asyncio.gather(*(self._shutdown_worker(handle) for handle in handles))

    async def close(self) -> None:
        self.closed = True
        if self._monitor is not None:
            self._monitor.cancel()
        # Recoveries too: one waiting to try again would otherwise hold up the drain below
        tasks = [*self._background, *self._replacing.values(), *self._recoveries]
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        # Each worker stops its loops and finishes the commits under way, which this process's writer
        # still takes; then it answers and exits
        await self._shutdown_workers()
        await self._drain_operations()
        self._executor.shutdown(wait=True)
        for session in self.sessions.values():
            # No more frames will come: a 3D view waiting for one hears so now
            session.wake_frame_waiters()
        self._close_snapshot_feed()
