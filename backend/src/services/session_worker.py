"""
A session worker process: live training sessions ticking on a core of their own.

Author: Lau Xiang Yang
Date Created: 18/11/2026
Last Modified: 18/11/2026

Description:
    Phase 2 of the multi-core sessions plan. The gateway (session_gateway.GatewaySessionService, in the
    process that serves HTTP and owns the SQLite writer) starts a few of these and places each live
    session on one. A worker loads the scenario and road network once, freezes them out of the garbage
    collector's full collections (freeze_static_heap), and runs the same per-session loop as the
    in-process runtime (TrainingSessionService): stage a copy of the actor, tick it, commit, install.
    Its store is a StoreClient, so each commit is encoded here and written by the gateway's writer;
    the worker installs the staged actor and pushes its traffic frame only once the gateway answers
    that the commit is durable.

    A session's ticks run in order on one actor in one process, as before, so the number of workers
    changes no session's state. What crosses the pipe is plain data: commands and approvals as JSON,
    encoded publications, traffic frames as the JSON /traffic sends, and a progress report four times
    a second (each session's tick and its measured cost per tick, which the gateway places and admits
    sessions by, and this worker's tick lateness).

    The worker exits when the gateway asks it to, when the pipe to it ends, or when the gateway's
    process is gone (it watches the process, so a gateway that is killed leaves no worker behind).
"""

from __future__ import annotations

import asyncio
import logging
import os
from pathlib import Path
import sys
from threading import Thread
import time
from time import perf_counter_ns, process_time
from typing import Any

logger = logging.getLogger(__name__)
# How often a worker reports its sessions' progress; the gateway takes several missed reports in a row
# for a worker that has stopped answering
PROGRESS_S = 0.25
# How quickly a session's measured cost per tick follows a change: about the last 20 ticks
COST_ALPHA = 0.05


def runtime_settings() -> dict:
    """The process-wide scenario settings a worker takes from the gateway that starts it, so that a
    session runs the same in a worker as in the gateway's own process: whether new integrated sessions
    drive the background traffic, the scripted tutorial opening and the charge rate."""
    from src.services.integrated_actor import IntegratedSessionActor, get_workflow_definition
    definition = get_workflow_definition()
    return {"traffic": IntegratedSessionActor.TRAFFIC,
            "tutorial_opening": definition.incidents.simulated_catalog.policy["tutorial_opening"]["enabled"],
            "charge_time_scale": definition.data.get("charge_time_scale")}


def apply_runtime_settings(settings: dict) -> None:
    from src.services.integrated_actor import IntegratedSessionActor, get_workflow_definition
    definition = get_workflow_definition()
    IntegratedSessionActor.TRAFFIC = settings["traffic"]
    definition.incidents.simulated_catalog.policy["tutorial_opening"]["enabled"] = settings["tutorial_opening"]
    definition.data["charge_time_scale"] = settings["charge_time_scale"]


def memory_mib() -> dict:
    from src.services.session_scaling import memory_mib as measure
    return measure()


def _make_runtime_class():
    # Imported here, not at module level, so that starting a worker process imports nothing heavy
    # before its logging and parent watch are in place
    from src.services.integrated_actor import IntegratedSessionActor
    from src.services.training_sessions import LiveSession, TrainingSessionService

    class WorkerRuntime(TrainingSessionService):
        """The in-process runtime, hosting the sessions the gateway places here and telling the gateway
        what they do. It restores nothing by itself: the gateway says which sessions to restore."""

        def __init__(self, store, channel, *, manual_clock: bool = False):
            super().__init__(store)
            self.channel = channel
            self.manual_clock = manual_clock
            self._initialized = True
            # session ID -> [mean milliseconds a tick, ticks measured]
            self._costs: dict[str, list] = {}
            self._max_lateness_ms = 0.0
            self._missed_deadlines = 0

        async def _initialize(self) -> None:
            self._initialized = True

        async def _run(self, session: LiveSession) -> None:
            if self.manual_clock:
                # Diagnostic: ticks only when the gateway asks (step), so a run can be compared exactly
                await asyncio.Event().wait()
            await super()._run(session)

        def _step_staged(self, actor, baseline, enqueued_ns=None):
            started = perf_counter_ns()
            try:
                return super()._step_staged(actor, baseline, enqueued_ns)
            finally:
                # The whole staged tick, its commit's round trip included: what admitting another
                # session here must leave room for
                elapsed = (perf_counter_ns() - started) / 1e6
                cost = self._costs.setdefault(actor.session_id, [0.0, 0])
                cost[0] = elapsed if cost[1] == 0 else cost[0] + COST_ALPHA * (elapsed - cost[0])
                cost[1] += 1

        def _frame_added(self, session: LiveSession, tick: int, frame: str) -> None:
            # Kept by the gateway, which answers /traffic; this process keeps none
            self.channel.send("frame", (session.actor.session_id, tick, frame))

        def _session_failed(self, session: LiveSession) -> None:
            self.channel.send("state", {"session_id": session.actor.session_id, "failed": True})

        def _session_removed(self, session_id: str) -> None:
            self._costs.pop(session_id, None)
            self.channel.send("state", {"session_id": session_id, "removed": True})

        def _tick_scheduled(self, session: LiveSession, lateness_ns: int, missed: int, running: bool) -> None:
            if running:
                self._max_lateness_ms = max(self._max_lateness_ms, lateness_ns / 1e6)
                self._missed_deadlines += missed

        def describe(self, session: LiveSession) -> dict:
            """What the gateway keeps of a session placed here, with the traffic's frame as it stands, so
            a 3D view that asks at once has one."""
            actor = session.actor
            traffic = self._drives_traffic(session)
            return {"session_id": actor.session_id, "integrated": isinstance(actor, IntegratedSessionActor),
                    "traffic": traffic, "tick": actor.current_tick, "running": not actor.is_paused,
                    "frame": (actor.current_tick, self._frame_json(actor)) if traffic else None}

        def place(self, session: LiveSession) -> dict:
            self.sessions[session.actor.session_id] = session
            self._start(session)
            return self.describe(session)

        def restore_one(self, session_id: str) -> LiveSession | None:
            """Restores a session from its last committed checkpoint, as the in-process runtime restores
            each at startup. An unrecoverable one is committed FAILED and gives None."""
            from src.core.training_telemetry import boundary
            from src.persistence.training_store import TrainingStoreError
            from src.services.training_scene import SceneValidationError
            try:
                snapshot, state = self.store.runtime(session_id)
                with self.store.trace.span("runtime.session_recovery", **boundary(snapshot)):
                    return self._restore_session(snapshot, state)
            except (TrainingStoreError, SceneValidationError, ValueError, KeyError, TypeError, IndexError,
                    OverflowError) as error:
                if isinstance(error, TrainingStoreError) and error.code != "CORRUPT_CHECKPOINT":
                    raise
                logger.exception("Cannot recover training runtime for %s", session_id)
                self._commit_failure(session_id, reason="CORRUPT_CHECKPOINT")
                return None

        def progress(self) -> dict:
            sessions = {}
            for session_id, session in list(self.sessions.items()):
                cost = self._costs.get(session_id, (0.0, 0))
                sessions[session_id] = {"tick": session.actor.current_tick, "failed": session.failed,
                                        "running": not session.actor.is_paused and not session.failed,
                                        "cost_ms": cost[0], "samples": cost[1]}
            report = {"sessions": sessions, "max_lateness_ms": self._max_lateness_ms,
                      "missed_deadlines": self._missed_deadlines}
            self._max_lateness_ms, self._missed_deadlines = 0.0, 0
            return report

    return WorkerRuntime


def _watch_parent(pid: int) -> None:
    """Ends this process as soon as the gateway's is gone, however it ended."""
    def exit_now():
        os._exit(0)

    if os.name == "nt":
        import ctypes
        from ctypes import wintypes
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.OpenProcess.restype = wintypes.HANDLE
        kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel32.WaitForSingleObject.restype = wintypes.DWORD
        kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        # Held for this process's life, so a reused process ID cannot stand in for the gateway
        handle = kernel32.OpenProcess(0x00100000, False, pid)  # SYNCHRONIZE
        if not handle:
            exit_now()

        def watch():
            kernel32.WaitForSingleObject(handle, 0xFFFFFFFF)
            exit_now()
    else:
        def watch():
            while os.getppid() == pid:
                time.sleep(1.0)
            exit_now()
    Thread(target=watch, name="gateway-watch", daemon=True).start()


def _configure_logging(index: int, log_path: str | None) -> None:
    if log_path is not None:
        # The gateway writes its own output to a file (Tools/bundled_backend.py): this worker writes
        # beside it, to a file of its own so their lines never interleave
        path = Path(log_path)
        sys.stdout = sys.stderr = open(path.with_name(f"{path.stem}.worker-{index}{path.suffix}"), "a",
                                       encoding="utf-8", buffering=1)
    if sys.stderr is not None:
        logging.basicConfig(level=logging.INFO, force=True,
                            format=f"%(asctime)s session-worker-{index} pid=%(process)d %(levelname)s "
                                   f"%(name)s: %(message)s")


def main(connection, config: dict) -> None:
    """The worker process's entry point (multiprocessing, spawn). config: index, parent_pid,
    database_path, settings (runtime_settings), trace_capacity, validate_publications, manual_clock,
    log_path."""
    _configure_logging(config["index"], config.get("log_path"))
    _watch_parent(config["parent_pid"])
    apply_runtime_settings(config["settings"])
    from src.services.training_sessions import freeze_static_heap
    freeze_static_heap()
    asyncio.run(_serve(connection, config))


async def _serve(connection, config: dict) -> None:
    from contracts.v2.models import DispatcherCommand
    from contracts.v2.route_edit import RouteApprovalRequest, RouteEditPreview, RouteEditRequest
    from contracts.v2.sessions import CreateSessionRequest
    from src.core.training_telemetry import TrainingTrace
    from src.persistence.store_client import StoreClient
    from src.services.integrated_actor import get_workflow_definition
    from src.services.session_ipc import Channel

    loop = asyncio.get_running_loop()
    stopping = asyncio.Event()
    shutdown_requests: list[int] = []
    runtime = None

    async def create(payload):
        request = CreateSessionRequest.model_validate_json(payload["request"])
        snapshot, session = await runtime._complete(
            runtime._admit(request, payload["actor_id"], payload["key"], payload["digest"]))
        return {"snapshot": snapshot.model_dump_json(), **runtime.describe(session)}

    async def restore(payload):
        session = await runtime._complete(asyncio.to_thread(runtime.restore_one, payload["session_id"]))
        if session is None:
            return {"session_id": payload["session_id"], "failed": True}
        return runtime.place(session)

    async def submit(payload):
        command = DispatcherCommand.model_validate_json(payload["command"])
        result = await runtime.submit(payload["actor_id"], command)
        return {"status": result.model_dump_json(), "removed": command.session_id not in runtime.sessions}

    async def approve_route(payload):
        request = RouteApprovalRequest.model_validate_json(payload["request"])
        if payload["preview"] is not None:
            draft, preview = payload["preview"]
            get_workflow_definition().routes.retain(RouteEditRequest.model_validate_json(draft),
                                                    RouteEditPreview.model_validate_json(preview))
        result = await runtime.approve_route(payload["actor_id"], payload["session_id"], request)
        return {"status": result.model_dump_json(), "removed": payload["session_id"] not in runtime.sessions}

    async def signal_state(payload):
        return runtime.signal_state(payload["session_id"])

    async def step(payload):
        session = runtime.sessions[payload["session_id"]]
        for _ in range(payload["ticks"]):
            await runtime.step(session)
        return session.actor.current_tick

    async def trace(payload):
        return runtime.store.trace.export()

    async def status(payload):
        return {"pid": os.getpid(), "cpu_s": process_time(), "memory": memory_mib(),
                "sessions": sorted(runtime.sessions)}

    handlers = {"create": create, "restore": restore, "submit": submit, "approve_route": approve_route,
                "signal_state": signal_state, "step": step, "trace": trace, "status": status}

    def on_message(kind: str, request_id: int, payload: Any) -> None:
        if kind == "shutdown":
            shutdown_requests.append(request_id)
            loop.call_soon_threadsafe(stopping.set)
            return
        future = asyncio.run_coroutine_threadsafe(handlers[kind](payload), loop)
        if request_id:
            channel.answer(request_id, future)

    channel = Channel(connection, name=f"session-worker-{config['index']}", on_message=on_message,
                      on_closed=lambda: loop.call_soon_threadsafe(stopping.set))
    store = StoreClient(config["database_path"], channel.call, trace=TrainingTrace(config["trace_capacity"]),
                        validate_publications=config["validate_publications"])
    runtime = _make_runtime_class()(store, channel, manual_clock=config["manual_clock"])

    async def report_progress():
        while True:
            channel.send("progress", runtime.progress())
            await asyncio.sleep(PROGRESS_S)

    channel.send("ready", {"pid": os.getpid()})
    reporter = asyncio.create_task(report_progress())
    await stopping.wait()
    reporter.cancel()
    # As the in-process runtime closes: loops stop, then commits already under way finish
    await runtime.close()
    store.close()
    for request_id in shutdown_requests:
        channel.reply(request_id, {"pid": os.getpid()})
    channel.close()
    channel.flush(5)
