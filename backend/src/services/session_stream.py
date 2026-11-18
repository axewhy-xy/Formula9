"""
Live push of a training session's snapshots and traffic frames over a WebSocket.

Author: Lau Xiang Yang
Date Created: 18/11/2026
Last Modified: 18/11/2026

Description:
    Phase 3 of the multi-core sessions plan. A client used to poll GET /snapshot once a second and
    long-poll GET /traffic ten times a second, so what it showed was up to a second old and every poll
    paid for a request. WS /api/v2/sessions/{session_id}/stream (api/routes/training_stream.py) pushes
    both instead, from what the gateway already holds: each live session's latest committed snapshot
    JSON (TrainingStore.published_snapshot_json) and its latest traffic frames (FrameFeed).

    SnapshotFeed is how a stream hears of a commit without polling: the store's writer calls it after
    each commit changes a session's published snapshot, and it wakes that session's streams on the
    event loop. SessionStream is one client's stream; the message shapes are in contracts/v2/stream.py.

    The stream only speeds delivery up. Snapshots still coalesce (a stream sends the newest when it
    may, never an older one after a newer), durable events are still read from GET /events, commands
    still go over HTTP, and anything the stream cannot say (the session recovering, deleted, the login
    gone) ends it with an error, after which the client polls over HTTP, as before, and reconnects.
"""

from __future__ import annotations

import asyncio
from contextlib import contextmanager
import json
import logging
import time
from typing import TYPE_CHECKING, Any, Iterator

import anyio

if TYPE_CHECKING:
    from src.persistence.training_store import TrainingStore

logger = logging.getLogger(__name__)

# How often a stream may send a snapshot unless the client asks otherwise, and the bounds it may ask
# within. Sessions commit ten a second; a client showing panels and a map needs fewer.
SNAPSHOT_INTERVAL_MS = 250
MIN_SNAPSHOT_INTERVAL_MS = 100
MAX_SNAPSHOT_INTERVAL_MS = 5000
# A stream with nothing to send (a paused or ended session) says it is still there this often, so a
# client can tell a quiet session from a lost connection
HEARTBEAT_S = 5.0
# How often a stream with no new snapshot checks that its session is still available
AVAILABILITY_CHECK_S = 1.0
# How long a stream waits for the next traffic frame before looking again
TRAFFIC_WAIT_S = 1.0
# How long a stream for a session with no traffic (none at all, or none while it recovers) waits
# before asking again
NO_TRAFFIC_RETRY_S = 2.0
# WebSocket close codes: the backend going away, a session gone, login refused, a session not
# available for now (try again later), anything else
CLOSE_GOING_AWAY = 1001
CLOSE_TRY_AGAIN_LATER = 1013
CLOSE_INTERNAL_ERROR = 1011
CLOSE_NOT_FOUND = 4404
CLOSE_FORBIDDEN = 4403
_RETRYABLE = {"STALE_REVISION", "QUEUE_FULL", "SESSION_RECOVERING"}


class SnapshotFeed:
    """Wakes the streams of a session when a commit changes its published snapshot.

    The store calls _published on its writer thread; the wake runs on the event loop the feed was made
    on. A stream takes signal(session_id) before it reads the snapshot, so no commit comes between the
    read and the wait unheard. Only sessions with a stream subscribed are woken.
    """

    def __init__(self, store: "TrainingStore", loop: asyncio.AbstractEventLoop):
        self._store = store
        self._loop = loop
        self._signals: dict[str, asyncio.Event] = {}
        self._subscribers: dict[str, int] = {}
        self.closed = False
        store.add_publish_listener(self._published)

    def _published(self, session_id: str) -> None:
        # On the store's writer thread. Reading the dict is safe there; it is only changed on the loop.
        if session_id not in self._subscribers:
            return
        try:
            self._loop.call_soon_threadsafe(self._wake, session_id)
        except RuntimeError:  # The loop has closed: nobody is listening any more
            pass

    def _wake(self, session_id: str) -> None:
        signal = self._signals.pop(session_id, None)
        if signal is not None:
            signal.set()

    def signal(self, session_id: str) -> asyncio.Event:
        """The event set at the session's next commit, or when the feed closes: take it before reading."""
        if self.closed:
            ended = asyncio.Event()
            ended.set()
            return ended
        signal = self._signals.get(session_id)
        if signal is None:
            signal = self._signals[session_id] = asyncio.Event()
        return signal

    @contextmanager
    def subscribe(self, session_id: str) -> Iterator["SnapshotFeed"]:
        """While open, the session's commits wake its signal."""
        self._subscribers[session_id] = self._subscribers.get(session_id, 0) + 1
        try:
            yield self
        finally:
            remaining = self._subscribers[session_id] - 1
            if remaining:
                self._subscribers[session_id] = remaining
            else:
                del self._subscribers[session_id]
                self._signals.pop(session_id, None)

    @property
    def streams(self) -> int:
        return sum(self._subscribers.values())

    def close(self) -> None:
        """No more commits will be heard: every stream waiting wakes, and finds the feed closed."""
        if self.closed:
            return
        self.closed = True
        self._store.remove_publish_listener(self._published)
        signals, self._signals = self._signals, {}
        for signal in signals.values():
            signal.set()


def clamp_interval_ms(value: int | None) -> int:
    """The snapshot interval a stream uses for what a client asked."""
    if value is None:
        return SNAPSHOT_INTERVAL_MS
    return max(MIN_SNAPSHOT_INTERVAL_MS, min(MAX_SNAPSHOT_INTERVAL_MS, int(value)))


def error_message(session_id: str, code: str, message: str) -> str:
    return json.dumps({"type": "error", "session_id": session_id, "error": {
        "code": code, "message": message[:512], "retryable": code in _RETRYABLE}}, separators=(",", ":"))


def close_code(code: str) -> int:
    if code == "INVALID_REFERENCE":
        return CLOSE_NOT_FOUND
    if code == "ROLE_DENIED":
        return CLOSE_FORBIDDEN
    if code in _RETRYABLE:
        return CLOSE_TRY_AGAIN_LATER
    return CLOSE_INTERNAL_ERROR


class StreamClosed(Exception):
    """Ends a stream: with a close code to send, or none when the client has gone."""

    def __init__(self, code: int | None = None, reason: str = ""):
        super().__init__(reason)
        self.code = code
        self.reason = reason


class SessionStream:
    """One client's stream of a session: the newest committed snapshot at most once an interval, the
    traffic frames as they are made (while the client wants them), a heartbeat while there is neither,
    and the client's traffic controls. The websocket is a Starlette WebSocket, already accepted; the
    service a SessionServiceBase (training_sessions), in process or the gateway."""

    def __init__(self, websocket: Any, service: Any, session_id: str, *, traffic: bool = False,
                 after_tick: int = -1, snapshot_interval_ms: int | None = None):
        self.websocket = websocket
        self.service = service
        self.session_id = session_id
        self.interval_s = clamp_interval_ms(snapshot_interval_ms) / 1000
        self.traffic_enabled = traffic
        self.traffic_cursor = max(-1, after_tick)
        # Set, and replaced, when the client changes what traffic it wants
        self._traffic_changed = asyncio.Event()
        self._send_lock = asyncio.Lock()
        self._last_sent = time.monotonic()
        self.snapshots_sent = 0
        self.frames_sent = 0

    async def run(self) -> None:
        """Streams until the client leaves, the session can no longer be streamed or the backend stops.

        The three parts run in an anyio task group, as the ASGI server's own cancellation (the client
        or the server going away) is anyio's: whichever part ends first ends the others."""
        closed: StreamClosed | None = None

        async def part(work) -> None:
            nonlocal closed
            try:
                await work()
            except StreamClosed as error:
                closed = closed or error
            except Exception:
                logger.exception("Session stream failed")
                closed = closed or StreamClosed(CLOSE_INTERNAL_ERROR, "Stream failed")
            finally:
                tasks.cancel_scope.cancel()

        async with anyio.create_task_group() as tasks:
            tasks.start_soon(part, self._snapshots, name="stream-snapshots")
            tasks.start_soon(part, self._traffic, name="stream-traffic")
            tasks.start_soon(part, self._controls, name="stream-controls")
        if closed is not None and closed.code is not None:
            try:
                await self.websocket.close(code=closed.code, reason=closed.reason[:120])
            except Exception:  # The client went first
                pass

    async def send(self, text: str) -> None:
        async with self._send_lock:
            try:
                await self.websocket.send_text(text)
            except Exception as error:  # The client has gone, however the server says so
                raise StreamClosed() from error
            self._last_sent = time.monotonic()

    async def fail(self, code: str, message: str) -> None:
        """Tells the client why the stream ends, then ends it."""
        await self.send(error_message(self.session_id, code, message))
        raise StreamClosed(close_code(code), code)

    def _check_open(self) -> None:
        if self.service.closed or self.service.snapshot_feed().closed:
            raise StreamClosed(CLOSE_GOING_AWAY, "Backend shutting down")

    async def _latest(self, fresh: bool) -> str | None:
        """The session's newest committed snapshot JSON. Unless fresh (just woken by a commit, or the
        first look), only what is in memory: a session not live (archived, say) is not read from the
        database every check. Raises TrainingStoreError when the session cannot be streamed."""
        if fresh:
            return await self.service.snapshot_json(self.session_id)
        return self.service.live_snapshot_json(self.session_id)

    async def _snapshots(self) -> None:
        from src.persistence.training_store import TrainingStoreError
        feed = self.service.snapshot_feed()
        with feed.subscribe(self.session_id):
            self.service.store.trace.high_water("live_streams", feed.streams)
            sent: str | None = None
            sent_at = float("-inf")
            fresh = True
            while True:
                self._check_open()
                signal = feed.signal(self.session_id)
                try:
                    raw = await self._latest(fresh)
                except TrainingStoreError as error:
                    await self.fail(error.code, str(error))
                if raw is not None and raw is not sent and raw != sent:
                    wait = sent_at + self.interval_s - time.monotonic()
                    if wait > 0:
                        # Too soon after the last: wait out the interval, then send whatever is newest
                        await asyncio.sleep(wait)
                        fresh = True
                        continue
                    await self.send('{"type":"snapshot","session_id":%s,"snapshot":%s}'
                                    % (json.dumps(self.session_id), raw))
                    sent, sent_at = raw, time.monotonic()
                    self.snapshots_sent += 1
                elif time.monotonic() - self._last_sent >= HEARTBEAT_S:
                    await self.send('{"type":"heartbeat","session_id":%s}' % json.dumps(self.session_id))
                try:
                    await asyncio.wait_for(signal.wait(), AVAILABILITY_CHECK_S)
                    fresh = True
                except (TimeoutError, asyncio.TimeoutError):
                    fresh = False

    async def _wait_traffic_change(self, timeout: float | None) -> None:
        try:
            await asyncio.wait_for(self._traffic_changed.wait(), timeout)
        except (TimeoutError, asyncio.TimeoutError):
            pass

    async def _traffic(self) -> None:
        from src.persistence.training_store import TrainingStoreError
        while True:
            self._check_open()
            if not self.traffic_enabled:
                await self._wait_traffic_change(None)
                continue
            cursor = self.traffic_cursor
            try:
                entries = await self.service.wait_for_traffic_entries(self.session_id, cursor, TRAFFIC_WAIT_S)
                newest = None if entries else self.service.newest_traffic_tick(self.session_id)
            except TrainingStoreError:
                # No traffic for this session now: none at all, or none while it recovers. The snapshot
                # side reports a session that cannot be streamed.
                await self._wait_traffic_change(NO_TRAFFIC_RETRY_S)
                continue
            self._check_open()
            if not self.traffic_enabled or cursor != self.traffic_cursor:
                continue  # The client changed what it wants while this waited
            if entries:
                self.traffic_cursor = entries[-1][0]
                await self.send('{"type":"traffic","session_id":%s,"frames":[%s]}'
                                % (json.dumps(self.session_id), ",".join(frame for _, frame in entries)))
                self.frames_sent += len(entries)
            elif newest is not None and newest < cursor:
                # The session's clock went back (a recovery restored an earlier checkpoint): start over
                self.traffic_cursor = -1

    async def _controls(self) -> None:
        """The client's messages: {"type": "traffic", "enabled": bool, "after_tick": int (optional)}."""
        while True:
            message = await self.websocket.receive()
            if message["type"] == "websocket.disconnect":
                raise StreamClosed()
            text = message.get("text")
            try:
                control = json.loads(text) if text is not None else None
            except ValueError:
                control = None
            after_tick = control.get("after_tick") if isinstance(control, dict) else None
            if not isinstance(control, dict) or control.get("type") != "traffic" \
                    or not isinstance(control.get("enabled"), bool) \
                    or (after_tick is not None and (not isinstance(after_tick, int) or isinstance(after_tick, bool))):
                # A message this backend does not know is refused, but the stream goes on
                await self.send(error_message(self.session_id, "INVALID_PAYLOAD", "Unsupported stream control"))
                continue
            self.traffic_enabled = control["enabled"]
            if after_tick is not None:
                self.traffic_cursor = max(-1, after_tick)
            changed, self._traffic_changed = self._traffic_changed, asyncio.Event()
            changed.set()
