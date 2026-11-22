"""One end of the duplex pipe between the gateway process and a session worker process.

Messages are (kind, request_id, payload) tuples of plain data: strings, numbers, dicts, lists and the
store's encoded dataclasses (EncodedCommit), never Pydantic objects. A request has a nonzero request_id
and is answered by a ("reply", request_id, (ok, value)) message; anything else is one-way.

A thread reads and a thread writes, so neither end's event loop nor the threads its ticks run on ever
block on the pipe: sending queues the message, and the reader hands each message to on_message on the
reader thread, which must not block for long (it queues work elsewhere). When the other end goes away
the reader sees the pipe end, every request still waiting fails with ChannelClosed, and on_closed runs
once, on the reader thread.
"""

from __future__ import annotations

from concurrent.futures import Future, InvalidStateError
import itertools
import logging
import queue
from threading import Lock, Thread
from typing import Any, Callable

from src.persistence.training_store import TrainingStoreError

logger = logging.getLogger(__name__)
# How long a store call waits for the gateway: far longer than any commit, so it only ends a call to a
# gateway that is stuck rather than gone (a gateway that is gone closes the pipe, which ends it at once)
CALL_TIMEOUT_S = 120.0


class ChannelClosed(TrainingStoreError):
    """The other process went away, or this end was closed, before an answer came."""

    def __init__(self, message: str = "Session worker channel closed"):
        super().__init__("SESSION_RECOVERING", message)


def encode_error(error: BaseException) -> tuple:
    if isinstance(error, TrainingStoreError):
        return ("store", error.code, str(error), error.current_revision)
    return ("other", type(error).__name__, str(error)[:1000], None)


def decode_error(value: tuple) -> BaseException:
    kind, code, message, current_revision = value
    if kind == "store":
        return TrainingStoreError(code, message, current_revision=current_revision)
    return RuntimeError(f"{code} in the other process: {message}")


class Channel:
    def __init__(self, connection, *, name: str, on_message: Callable[[str, int, Any], None],
                 on_closed: Callable[[], None] | None = None):
        self.name = name
        self._connection = connection
        self._on_message = on_message
        self._on_closed = on_closed
        self._outbox: queue.SimpleQueue = queue.SimpleQueue()
        self._pending: dict[int, Future] = {}
        self._ids = itertools.count(1)
        self._lock = Lock()
        self.closed = False
        self._reader = Thread(target=self._read, name=f"{name}-reader", daemon=True)
        self._writer = Thread(target=self._write, name=f"{name}-writer", daemon=True)
        self._reader.start()
        self._writer.start()

    def send(self, kind: str, payload: Any = None, request_id: int = 0) -> None:
        """Queue a one-way message. Dropped once the channel is closed."""
        if not self.closed:
            self._outbox.put((kind, request_id, payload))

    def request(self, kind: str, payload: Any = None) -> Future:
        """Send a request; the future completes with the other end's answer or its error."""
        future: Future = Future()
        with self._lock:
            if self.closed:
                future.set_exception(ChannelClosed())
                return future
            request_id = next(self._ids)
            self._pending[request_id] = future
        self._outbox.put((kind, request_id, payload))
        return future

    def call(self, kind: str, payload: Any = None, timeout: float | None = CALL_TIMEOUT_S) -> Any:
        """request, waiting for the answer on this thread."""
        return self.request(kind, payload).result(timeout)

    def reply(self, request_id: int, value: Any = None, error: BaseException | None = None) -> None:
        self.send("reply", (False, encode_error(error)) if error is not None else (True, value), request_id)

    def answer(self, request_id: int, future: Future) -> None:
        """Reply with a concurrent future's outcome once it completes."""
        def done(completed: Future) -> None:
            error = completed.exception()
            self.reply(request_id, None if error is not None else completed.result(), error)
        future.add_done_callback(done)

    def _read(self) -> None:
        try:
            while True:
                kind, request_id, payload = self._connection.recv()
                if kind == "reply":
                    with self._lock:
                        future = self._pending.pop(request_id, None)
                    if future is not None:
                        ok, value = payload
                        try:
                            if ok:
                                future.set_result(value)
                            else:
                                future.set_exception(decode_error(value))
                        except InvalidStateError:
                            pass  # The caller stopped waiting (cancelled) before the answer came
                    continue
                try:
                    self._on_message(kind, request_id, payload)
                except Exception as error:
                    logger.exception("%s could not handle %s", self.name, kind)
                    if request_id:
                        self.reply(request_id, error=error)
        except (EOFError, OSError):
            pass
        except Exception:
            logger.exception("%s stopped reading", self.name)
        self._close()
        if self._on_closed is not None:
            self._on_closed()

    def _write(self) -> None:
        while (item := self._outbox.get()) is not None:
            try:
                self._connection.send(item)
            except (OSError, ValueError):
                # The other end is gone; the reader sees the same and closes the channel
                self._close()
                break
            except Exception:
                # A message that cannot be pickled must not stop the channel; its request fails instead
                logger.exception("%s could not send %s", self.name, item[0])
                if item[1] and item[0] != "reply":
                    with self._lock:
                        future = self._pending.pop(item[1], None)
                    if future is not None:
                        try:
                            future.set_exception(RuntimeError(f"Could not send {item[0]}"))
                        except InvalidStateError:
                            pass

    def _close(self) -> None:
        with self._lock:
            if self.closed:
                return
            self.closed = True
            pending, self._pending = self._pending, {}
        self._outbox.put(None)
        for future in pending.values():
            try:
                future.set_exception(ChannelClosed())
            except InvalidStateError:
                pass

    def close(self) -> None:
        """Stop sending and fail every request still waiting. The reader stops when the pipe ends."""
        self._close()

    def flush(self, timeout: float | None = None) -> None:
        """After close: wait for the messages queued before it to be sent."""
        self._writer.join(timeout)

    def join(self, timeout: float | None = None) -> None:
        """Wait for both threads; the reader ends only when the other end has gone."""
        self._reader.join(timeout)
        self._writer.join(timeout)
