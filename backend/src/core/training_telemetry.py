"""
Bounded runtime instrumentation for BE-M2-06 training sessions.

Author: Nur Syazliyarni Binte Zazali
Date Created: 28/10/2026
Last Modified: 28/10/2026

Description:
    Records monotonic timings, request/command/session correlation, failures
    and queue high-water marks in a bounded, thread-safe buffer. ASGI middleware
    links response publication to nested backend work through a trace header.
    Diagnostic data stays outside authoritative snapshots, checkpoints and
    hashes. Callers supply IDs and counters, never credentials, command payloads,
    reasons or exception text. Export outside the simulation step; ring overflow
    is counted explicitly.
    Each live runtime also keeps constant-size progress/commit timing state.
    Its monotonic observations measure active simulation progress and server
    state age without adding timestamps to authoritative data or checkpoints.

"""

from collections import Counter, deque
from contextlib import contextmanager
from contextvars import ContextVar
from threading import Lock
from time import perf_counter_ns
from uuid import uuid4

_context = ContextVar("training_trace", default={})


@contextmanager
def correlate(**tags):
    """Attach diagnostic identifiers to this context and restore it on exit."""
    token = _context.set({**_context.get(), **tags})
    try:
        yield
    finally:
        _context.reset(token)


def boundary(value):
    """Extract session, epoch, tick and event-cursor tags from a v2 message."""
    return {key: getattr(value, key) for key in
            ("session_id", "session_epoch", "tick", "event_sequence")}


class TrainingTrace:
    """Keep the latest spans and cumulative counts without growing the buffer."""
    def __init__(self, capacity=8192, *, clock=perf_counter_ns):
        if type(capacity) is not int or capacity < 1:
            raise ValueError("Trace capacity must be a positive integer")
        self.capacity = capacity
        self.run_id = uuid4().hex
        self.clock = clock
        self._records = deque(maxlen=capacity)
        self._lock = Lock()
        self._sequence = 0
        self._counts = Counter()
        self._high_water = Counter()

    @contextmanager
    def span(self, name, **tags):
        """Measure work; yielded fields can add the final status or committed IDs."""
        fields = {**_context.get(), **tags}
        start = self.clock()
        try:
            yield fields
        except BaseException as error:
            fields.update(outcome="ERROR", error_code=getattr(error, "code", type(error).__name__))
            raise
        finally:
            self.record(name, start, self.clock(), **fields)

    def record(self, name, start_ns, end_ns, **tags):
        """Append a monotonic span or zero-duration diagnostic point."""
        if end_ns < start_ns:
            raise ValueError("Monotonic trace clock moved backwards")
        with self._lock:
            self._sequence += 1
            self._counts[name] += 1
            self._records.append({**_context.get(), **tags,
                                  "name": name, "run_id": self.run_id, "sequence": self._sequence,
                                  "start_ns": start_ns, "end_ns": end_ns,
                                  "duration_ms": (end_ns - start_ns) / 1e6,
                                  "outcome": tags.get("outcome", "OK")})

    def high_water(self, name, value):
        """Retain the largest observed value of a named resource counter."""
        with self._lock:
            self._high_water[name] = max(self._high_water[name], value)

    def export(self):
        """Copy retained records and loss/counter metadata without draining them."""
        with self._lock:
            return {"run_id": self.run_id, "clock": "perf_counter_ns", "capacity": self.capacity,
                    "total_records": self._sequence, "dropped_records": self._sequence - len(self._records),
                    "counts": dict(self._counts), "high_water": dict(self._high_water),
                    "records": [dict(record) for record in self._records]}


class SessionProgress:
    """Session-lock-owned diagnostic state; never serialize or recover it.

    A sample's origin is the beginning of its physics step. A command at the
    same tick preserves that origin. Recovery starts with unknown source age
    because a prior process's monotonic clock cannot establish current age.
    The peak age is a conservative bound just before replacing a committed
    frame, observed after the write and actor installation finish.
    """
    def __init__(self):
        self.epoch = None
        self.tick = None
        self.observed_ns = None
        self.running = False
        self.actor_sample_ns = None
        self.committed_tick = None
        self.committed_sequence = None
        self.committed_sample_ns = None

    def observe(self, trace, actor, *, committed=None, sample_ns=None, running=None):
        now = trace.clock()
        epoch, tick = actor.session_epoch, actor.current_tick
        continuous = self.epoch == epoch and self.tick is not None and tick >= self.tick
        if not continuous:
            self.actor_sample_ns = self.committed_sample_ns = None
            self.committed_tick = self.committed_sequence = None
        previous_origin = self.committed_sample_ns
        if sample_ns is not None and (not continuous or tick > self.tick):
            self.actor_sample_ns = sample_ns
        if committed is not None and committed.session_epoch == epoch:
            if committed.tick != self.committed_tick:
                self.committed_sample_ns = self.actor_sample_ns if committed.tick == tick else None
            self.committed_tick = committed.tick
            self.committed_sequence = committed.event_sequence
        def age(origin):
            return (now-origin)/1e6 if origin is not None else None
        current_age, previous_age = age(self.committed_sample_ns), age(previous_origin)
        peak_age = max(current_age, previous_age) if current_age is not None and previous_age is not None else None
        active = not actor.is_paused if running is None else running
        trace.record("runtime.progress", now, now, session_id=actor.session_id,
            session_epoch=epoch, tick=tick, event_sequence=self.committed_sequence,
            running=active, interval_running=continuous and self.running,
            interval_start_ns=self.observed_ns if continuous else None,
            simulation_advance_s=(tick-self.tick)*actor.STEP_S if continuous else 0.0,
            committed_tick=self.committed_tick,
            committed_state_age_ms=current_age, oldest_committed_state_age_ms=peak_age,
            committed_tick_lag=tick-self.committed_tick if self.committed_tick is not None else None)
        self.epoch, self.tick, self.observed_ns, self.running = epoch, tick, now, active


class TrainingTraceMiddleware:
    """Time through the final ASGI send; this is server publication, not paint.

    The router binds its selected store to the scope, including in tests with
    dependency overrides. Thread workers inherit request correlation.
    """
    def __init__(self, app):
        self.app = app

    async def __call__(self, scope, receive, send):
        if scope["type"] != "http":
            return await self.app(scope, receive, send)
        start = perf_counter_ns()
        request_id = uuid4().hex
        status, size, complete = 500, 0, False

        async def traced_send(message):
            nonlocal status, size, complete
            if message["type"] == "http.response.start":
                status = message["status"]
                if "training_trace" in scope:
                    message = {**message, "headers": [*message.get("headers", []),
                               (b"x-training-trace-id", request_id.encode("ascii"))]}
            if message["type"] == "http.response.body":
                size += len(message.get("body", b""))
            await send(message)
            if message["type"] == "http.response.body" and not message.get("more_body", False):
                complete = True

        with correlate(request_id=request_id):
            try:
                await self.app(scope, receive, traced_send)
            finally:
                trace = scope.get("training_trace")
                if trace is not None:
                    route = scope.get("route")
                    trace.record("http.publication", start, perf_counter_ns(),
                                 route=getattr(route, "path", "UNMATCHED"), method=scope["method"],
                                 session_id=scope.get("path_params", {}).get("session_id"),
                                 status_code=status, response_bytes=size, response_complete=complete,
                                 outcome="OK" if complete and status < 400 else "HTTP_ERROR" if complete else "TRANSPORT_ERROR")
