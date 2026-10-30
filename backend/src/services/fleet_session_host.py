"""Single-event-loop ownership and immutable, coalesced fleet presentation.

This headless host is independent of HTTP and the durable TrainingStore. A live
training adapter must commit commands/state before publishing to its clients.
"""

from __future__ import annotations

import asyncio
from dataclasses import dataclass, field
import logging

from ..schemas.shuttle_data import Command
from .session_actor import SessionActor

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class FleetPublication:
    session_id: str
    command_tick: int
    snapshot_json: str | None
    error_code: str | None = None


@dataclass
class _HostedSession:
    actor: SessionActor
    latest: FleetPublication
    task: asyncio.Task[None] | None = None
    subscribers: dict[int, asyncio.Queue[FleetPublication]] = field(default_factory=dict)
    next_subscriber_id: int = 0
    missed_deadlines: int = 0


class FleetSessionHost:
    """Own one actor/task per ID, regardless of attached presentation clients.

    All methods must run on the event loop that created the host's sessions.
    Subscribing and reading never tick an actor or create a background task.
    """

    MAX_SESSIONS = 16
    MAX_SUBSCRIBERS = 16

    def __init__(self) -> None:
        self._sessions: dict[str, _HostedSession] = {}
        self._closed = False
        self._loop: asyncio.AbstractEventLoop | None = None

    def _check_owner(self) -> None:
        loop = asyncio.get_running_loop()
        if self._loop is None:
            self._loop = loop
        if loop is not self._loop:
            raise RuntimeError("Fleet host belongs to a different event loop")
        if self._closed:
            raise RuntimeError("Fleet host is closed")

    def create_session(self, session_id: str, *, seed: int, routes: list[dict]) -> FleetPublication:
        self._check_owner()
        if session_id in self._sessions:
            raise ValueError("Fleet session already exists")
        if len(self._sessions) >= self.MAX_SESSIONS:
            raise ValueError("Fleet session limit reached")
        actor = SessionActor(seed=seed, route=routes, session_id=session_id)
        latest = self._snapshot(actor)
        session = _HostedSession(actor, latest)
        self._sessions[session_id] = session
        session.task = asyncio.create_task(self._run(session), name=f"fleet-session-{session_id}")
        return latest

    @staticmethod
    def _snapshot(actor: SessionActor) -> FleetPublication:
        # Serialize once; every subscriber receives the same immutable object.
        return FleetPublication(actor.session_id, actor.command_tick,
                                actor.generate_snapshot().model_dump_json())

    def latest(self, session_id: str) -> FleetPublication:
        self._check_owner()
        return self._sessions[session_id].latest

    def enqueue_command(self, session_id: str, command: Command) -> None:
        self._check_owner()
        session = self._sessions[session_id]
        if session.latest.error_code:
            raise RuntimeError("Fleet actor failed")
        session.actor.enqueue_command(command)

    def subscribe(self, session_id: str) -> tuple[int, asyncio.Queue[FleetPublication]]:
        self._check_owner()
        session = self._sessions[session_id]
        if len(session.subscribers) >= self.MAX_SUBSCRIBERS:
            raise ValueError("Fleet subscriber limit reached")
        subscriber_id = session.next_subscriber_id
        session.next_subscriber_id += 1
        queue: asyncio.Queue[FleetPublication] = asyncio.Queue(maxsize=1)
        queue.put_nowait(session.latest)
        session.subscribers[subscriber_id] = queue
        return subscriber_id, queue

    def unsubscribe(self, session_id: str, subscriber_id: int) -> None:
        self._check_owner()
        self._sessions[session_id].subscribers.pop(subscriber_id, None)

    @staticmethod
    def _publish(session: _HostedSession, publication: FleetPublication) -> None:
        session.latest = publication
        for queue in session.subscribers.values():
            if queue.full():
                queue.get_nowait()
            queue.put_nowait(publication)

    async def _run(self, session: _HostedSession) -> None:
        loop = asyncio.get_running_loop()
        deadline = loop.time() + SessionActor.STEP_S
        try:
            while True:
                await asyncio.sleep(max(0.0, deadline - loop.time()))
                session.actor.tick()
                if session.actor.should_emit_snapshot():
                    self._publish(session, self._snapshot(session.actor))
                deadline += SessionActor.STEP_S
                now = loop.time()
                if deadline <= now:
                    # Preserve 50 ms logical steps; avoid an unbounded catch-up
                    # burst when this process misses its wall-clock budget.
                    session.missed_deadlines += int((now - deadline) / SessionActor.STEP_S) + 1
                    deadline = now + SessionActor.STEP_S
        except asyncio.CancelledError:
            raise
        except Exception:
            # Wake consumers with a bounded terminal result instead of silently
            # leaving them on stale telemetry after an actor failure.
            logger.exception("Fleet actor failed for session %s at control step %s",
                             session.actor.session_id, session.actor.command_tick)
            self._publish(session, FleetPublication(session.actor.session_id,
                                                    session.actor.command_tick, None, "ACTOR_FAILED"))

    async def close(self) -> None:
        if self._closed:
            return
        self._check_owner()
        self._closed = True
        for session in self._sessions.values():
            self._publish(session, FleetPublication(session.actor.session_id,
                                                    session.actor.command_tick, None, "SESSION_CLOSED"))
        tasks = [session.task for session in self._sessions.values() if session.task is not None]
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        self._sessions.clear()
