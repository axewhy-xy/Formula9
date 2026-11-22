"""Single-writer SQLite journal for v2 training sessions.

The session actor stages a new snapshot and calls ``apply`` at a logical step.
The store publishes nothing until its transaction has committed. This module
does not run a simulation or infer domain effects from command payloads.
"""

from __future__ import annotations

import hashlib
import json
import logging
import os
import queue
import sqlite3
from concurrent.futures import Future
from contextlib import closing, contextmanager
from contextvars import copy_context
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from itertools import chain
from threading import Lock, Thread, local
from typing import BinaryIO, Callable, Iterator, TypeVar
from uuid import uuid4

from src.core.training_telemetry import TrainingTrace, boundary, correlate
from src.services.assessment import AssessmentPlan, finalize
from src.services.assessment_policy import AssessmentPolicy, configured_policy

from contracts.v2.assessment import SessionAssessment
from contracts.v2.models import (
    CommandStatus,
    DispatcherCommand,
    Event,
    EventReplay,
    FullSnapshot,
    ValidationFailure,
    transition_allowed,
    validate_command_against_snapshot,
)

T = TypeVar("T")
logger = logging.getLogger(__name__)
_MIGRATIONS = Path(__file__).with_name("migrations")
_OCCUPIES_CAPACITY = (
    "(s.status IN ('READY', 'PAUSED', 'RUNNING') AND"
    " (EXISTS (SELECT 1 FROM session_runtimes r WHERE r.session_id=s.session_id)"
    " OR EXISTS (SELECT 1 FROM session_creations c WHERE c.session_id=s.session_id)))"
)


class TrainingStoreError(Exception):
    def __init__(self, code: str, message: str, *, current_revision: int | None = None):
        super().__init__(message)
        self.code = code
        self.current_revision = current_revision


_TERMINAL = frozenset({"COMPLETED", "ARCHIVED", "FAILED"})


def _json(value: object) -> str:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def _snapshot_json(snapshot: FullSnapshot) -> str:
    """The snapshot as stored and as GET /snapshot sends it: Pydantic's own encoder, in field order.

    The same JSON as json.dumps of model_dump(mode="json"), a quarter of the time. Rows written before
    used sorted keys; each row's state_hash is of its own text, so both kinds stay readable."""
    return snapshot.model_dump_json()


def _hash(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def _boundary(snapshot: FullSnapshot) -> dict:
    """What a following publication must follow or keep (see _commit_snapshot), as JSON values."""
    return {
        "snapshot_id": snapshot.snapshot_id, "session_epoch": snapshot.session_epoch,
        "event_sequence": snapshot.event_sequence, "tick": snapshot.tick,
        "scene_revision": snapshot.scene_revision, "transform_revision": snapshot.transform_revision,
        "scenario_revision_id": snapshot.session.scenario_revision_id,
        "spatial": None if snapshot.spatial is None else snapshot.spatial.model_dump(mode="json"),
    }


@dataclass(frozen=True)
class _EncodedSnapshot:
    """A snapshot serialised once, before the writer takes it: the row's JSON, its hash and boundary."""
    raw: str
    digest: str
    boundary: dict
    status: str

    @classmethod
    def of(cls, snapshot: FullSnapshot) -> "_EncodedSnapshot":
        raw = _snapshot_json(snapshot)
        return cls(raw, _hash(raw), _boundary(snapshot), snapshot.session.status)

    @property
    def boundary_json(self) -> str:
        return _json({**self.boundary, "state_hash": self.digest})


@dataclass(frozen=True)
class _EncodedRuntime:
    """An actor checkpoint serialised before the writer takes it; traffic is (tick, JSON, hash) when the
    checkpoint carries the traffic's own (see TrainingStore._write_runtime)."""
    tick: int | None
    raw: str
    digest: str
    traffic: tuple[int, str, str] | None

    @classmethod
    def of(cls, state: "dict | _EncodedRuntime | None") -> "_EncodedRuntime | None":
        if state is None or isinstance(state, _EncodedRuntime):
            return state
        traffic = state.get("traffic")
        saved = None
        if traffic is not None:
            state = {key: value for key, value in state.items() if key != "traffic"}
            raw = _json(traffic)
            saved = (traffic["tick"], raw, _hash(raw))
        raw = _json(state)
        return cls(state.get("tick"), raw, _hash(raw), saved)


@dataclass(frozen=True)
class PreparedAssessment:
    """A grade computed outside the writer, tied to the exact journal and pinned plan it read."""
    snapshot_id: str
    plan_json: str
    result: dict


@dataclass(frozen=True)
class EncodedCommit:
    """A publication serialised before the writer takes it, as plain strings and numbers: the snapshot
    with its new snapshot ID, its domain events as (sequence, event_id, tick, JSON) and the actor
    checkpoint. A session worker process encodes it and the gateway's writer commits it
    (TrainingStore.submit_commit), so neither Pydantic objects nor their parsing cross the pipe."""
    session_id: str
    snapshot_id: str
    snapshot: _EncodedSnapshot
    events: tuple[tuple[int, str, int, str], ...]
    runtime: _EncodedRuntime | None
    expected_snapshot_id: str | None
    assessment: PreparedAssessment | None = None


def _encode_event(event: Event) -> tuple[int, str, int, str]:
    """An event as its row stores it: (sequence, event_id, tick, JSON)."""
    return event.event_sequence, event.event_id, event.tick, _json(event.model_dump(mode="json"))


def _now() -> str:
    return datetime.now(UTC).isoformat()


def _load_snapshot(row: sqlite3.Row) -> FullSnapshot:
    raw = row["snapshot_json"]
    if _hash(raw) != row["state_hash"]:
        raise TrainingStoreError("CORRUPT_CHECKPOINT", "Stored snapshot hash does not match")
    return FullSnapshot.model_validate_json(raw)


def _lock_database(path: Path) -> BinaryIO:
    handle = open(str(path) + ".lock", "a+b")
    try:
        handle.seek(0)
        if handle.read(1) == b"":
            handle.write(b"0")
            handle.flush()
        handle.seek(0)
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        return handle
    except OSError as error:
        handle.close()
        raise TrainingStoreError("SESSION_OWNED", "Training database is already owned by another process") from error


def _unlock_database(handle: BinaryIO) -> None:
    handle.seek(0)
    if os.name == "nt":
        import msvcrt
        msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
    else:
        import fcntl
        fcntl.flock(handle.fileno(), fcntl.LOCK_UN)
    handle.close()


def _target_revision(snapshot: FullSnapshot, target_type: str, target_id: str) -> int:
    if target_type == "SESSION":
        return snapshot.session.aggregate_revision
    values = {
        "ROUTE": ((r.route_id, r.revision) for r in snapshot.routes),
        "INCIDENT": ((i.incident_id, i.aggregate_revision) for i in snapshot.incidents),
        "CHARGER": ((c.charger_id, c.aggregate_revision) for c in snapshot.chargers),
        "VEHICLE": ((v.vehicle_id, v.aggregate_revision) for v in snapshot.vehicles),
    }[target_type]
    return next((revision for entity_id, revision in values if entity_id == target_id), 0)


class TrainingStore:
    """Bounded synchronous command interface backed by one SQLite writer thread.

    validate_publications has commit_snapshot check each publication against the contract, as it did
    by validating a copy before this store encoded publications once (the tests turn it on through
    F9_VALIDATE_PUBLICATIONS). Off by default: a publication is built from committed, validated state,
    and the check costs as much as encoding it.
    """

    owns_writer = True

    def __init__(self, path: Path, *, queue_size: int = 128, trace: TrainingTrace | None = None,
                 validate_publications: bool | None = None, assessment_policy: AssessmentPolicy | None = None):
        if queue_size < 1:
            raise ValueError("queue_size must be positive")
        if validate_publications is None:
            validate_publications = os.environ.get("F9_VALIDATE_PUBLICATIONS", "") not in {"", "0"}
        self.validate_publications = validate_publications
        self.assessment_policy = assessment_policy if assessment_policy is not None else configured_policy()
        self._assessment_owners: dict[str, str] = {}
        # The latest committed snapshot JSON of each session not terminal, set by the writer once a
        # commit is durable: GET /snapshot and snapshot() read it instead of the database. Only the
        # writer changes it, so it is never older than the row it stands for.
        self._published: dict[str, str] = {}
        # Called on the writer, with the session's ID, after each change to _published: how a live
        # stream (session_stream.SnapshotFeed) hears of a commit without polling. A listener must not
        # block, since every commit waits for it.
        self._publish_listeners: list[Callable[[str], None]] = []
        self.trace = trace if trace is not None else TrainingTrace()
        self.path = Path(path).resolve()
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._lock_file = _lock_database(self.path)
        self._jobs: queue.Queue = queue.Queue(maxsize=queue_size)
        # Each thread's read-only connection, kept open between reads (see _reading)
        self._readers = local()
        self._reader_connections: list[sqlite3.Connection] = []
        self._readers_lock = Lock()
        self._ready: Future[None] = Future()
        self._thread = Thread(target=self._run, name="training-sqlite-writer", daemon=True)
        self._thread.start()
        try:
            self._ready.result()
            with self.trace.span("store.open_recovery"):
                self._submit(self._initialize)
        except BaseException:
            self.close()
            raise

    def _run(self) -> None:
        try:
            connection = sqlite3.connect(self.path, isolation_level=None, timeout=10)
            connection.row_factory = sqlite3.Row
            connection.execute("PRAGMA journal_mode=WAL")
            # NORMAL in WAL mode: a commit is durable once it reaches the log, without an fsync of its
            # own; the log is synced when it is checkpointed into the database. A crash of this process
            # loses nothing committed, and the database stays consistent through anything; only a power
            # loss or an OS crash can roll back the last commits, which recovery treats like progress
            # not yet committed. FULL took one fsync per publication, ten a second per live session, on
            # the one writer thread.
            connection.execute("PRAGMA synchronous=NORMAL")
            connection.execute("PRAGMA foreign_keys=ON")
            self._ready.set_result(None)
        except BaseException as error:
            self._ready.set_exception(error)
            return
        try:
            while (job := self._jobs.get()) is not None:
                operation, future, context, enqueued, depth = job
                try:
                    def execute():
                        self.trace.record("writer.queue_wait", enqueued, self.trace.clock(), queue_depth=depth)
                        with self.trace.span("writer.transaction"):
                            return operation(connection)
                    future.set_result(context.run(execute))
                except BaseException as error:
                    if connection.in_transaction:
                        connection.rollback()
                    future.set_exception(error)
        finally:
            connection.close()

    def _submit(self, operation: Callable[[sqlite3.Connection], T]) -> T:
        return self._submit_async(operation).result()

    def _submit_async(self, operation: Callable[[sqlite3.Connection], T]) -> Future[T]:
        """Queue a writer job and return its future at once, without waiting for the transaction."""
        if self._lock_file.closed:
            raise TrainingStoreError("SESSION_OWNED", "Training store is closed")
        future: Future[T] = Future()
        try:
            depth = self._jobs.qsize() + 1
            self._jobs.put_nowait((operation, future, copy_context(), self.trace.clock(), depth))
            self.trace.high_water("writer_queue_depth", depth)
        except queue.Full as error:
            now = self.trace.clock()
            self.trace.record("writer.queue_rejected", now, now, outcome="ERROR", error_code="QUEUE_FULL",
                              queue_depth=self._jobs.maxsize)
            raise TrainingStoreError("QUEUE_FULL", "Training writer queue is full") from error
        return future

    def close(self) -> None:
        if self._thread.is_alive():
            self._jobs.put(None)
            self._thread.join()
        with self._readers_lock:
            readers, self._reader_connections = self._reader_connections, []
        for connection in readers:
            connection.close()
        if not self._lock_file.closed:
            _unlock_database(self._lock_file)

    def __enter__(self) -> "TrainingStore":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    def _initialize(self, connection: sqlite3.Connection) -> None:
        connection.execute("CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_at_utc TEXT NOT NULL)")
        current = connection.execute("SELECT COALESCE(MAX(version), 0) FROM schema_migrations").fetchone()[0]
        migrations = sorted(_MIGRATIONS.glob("[0-9][0-9][0-9][0-9]_*.sql"))
        latest = int(migrations[-1].name[:4]) if migrations else 0
        if current > latest:
            # Usually a branch with a newer migration opened this shared file first.
            raise TrainingStoreError(
                "SCHEMA_TOO_NEW",
                f"Training database {self.path} is at schema v{current}, but this backend only has "
                f"migrations up to v{latest}. A newer branch probably migrated it: switch back to that "
                "branch, or set TRAINING_DATABASE_PATH to a separate file for this one.",
            )
        # The assessment branch used v5 before main added the snapshot boundary in v5.
        # Upgrade those databases with main's schema before applying the combined v6.
        if current == 5 and "boundary_json" not in {row[1] for row in connection.execute("PRAGMA table_info(sessions)")}:
            script = (_MIGRATIONS / "0005_snapshot_boundary.sql").read_text(encoding="utf-8")
            connection.executescript("BEGIN IMMEDIATE;\n" + script + "\nCOMMIT;")
        for migration in migrations:
            version = int(migration.name[:4])
            if version <= current:
                continue
            script = migration.read_text(encoding="utf-8")
            connection.executescript("BEGIN IMMEDIATE;\n" + script + f"\nINSERT INTO schema_migrations VALUES ({version}, '{_now()}');\nCOMMIT;")
        self._recover(connection)
        self._assessment_owners = dict(connection.execute(
            "SELECT a.session_id, a.trainee_id FROM session_assessments a JOIN sessions s USING(session_id)"
            " WHERE s.status NOT IN ('COMPLETED', 'ARCHIVED', 'FAILED')"))

    @staticmethod
    def _write_snapshot(connection: sqlite3.Connection, snapshot: FullSnapshot, *, initial: bool = False,
                        encoded: _EncodedSnapshot | None = None) -> _EncodedSnapshot:
        """Writes the snapshot as the session's row and a checkpoint. encoded, when given, is the
        snapshot already serialised (_EncodedSnapshot.of), so the writer does not do it again."""
        if encoded is None:
            encoded = _EncodedSnapshot.of(snapshot)
        if initial:
            connection.execute(
                "INSERT INTO sessions (session_id, scenario_revision_id, epoch, status, tick, event_sequence,"
                " base_sequence, snapshot_json, state_hash, boundary_json) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                (snapshot.session_id, snapshot.session.scenario_revision_id, snapshot.session_epoch,
                 snapshot.session.status, snapshot.tick, snapshot.event_sequence, snapshot.event_sequence,
                 encoded.raw, encoded.digest, encoded.boundary_json),
            )
            TrainingStore._write_checkpoint(connection, snapshot.session_id, encoded)
        else:
            TrainingStore._write_encoded(connection, snapshot.session_id, encoded)
        return encoded

    @staticmethod
    def _write_encoded(connection: sqlite3.Connection, session_id: str, encoded: _EncodedSnapshot) -> None:
        """Writes an encoded snapshot over the session's row, and as a checkpoint: from its JSON and
        boundary alone, so the writer needs no snapshot object (submit_commit)."""
        boundary = encoded.boundary
        connection.execute(
            "UPDATE sessions SET epoch=?, status=?, tick=?, event_sequence=?, snapshot_json=?, state_hash=?,"
            " boundary_json=? WHERE session_id=?",
            (boundary["session_epoch"], encoded.status, boundary["tick"], boundary["event_sequence"],
             encoded.raw, encoded.digest, encoded.boundary_json, session_id),
        )
        TrainingStore._write_checkpoint(connection, session_id, encoded)

    @staticmethod
    def _write_checkpoint(connection: sqlite3.Connection, session_id: str, encoded: _EncodedSnapshot) -> None:
        boundary = encoded.boundary
        connection.execute(
            "INSERT INTO checkpoints VALUES (?, ?, ?, ?, ?, ?)",
            (session_id, boundary["event_sequence"], boundary["tick"], boundary["session_epoch"],
             encoded.raw, encoded.digest),
        )

    def _publish(self, session_id: str, encoded: _EncodedSnapshot | None) -> None:
        """After a commit, on the writer: the session's snapshot JSON for reads, or none once it ends."""
        if encoded is None or encoded.status in _TERMINAL:
            self._published.pop(session_id, None)
            self._assessment_owners.pop(session_id, None)
        else:
            self._published[session_id] = encoded.raw
        for listener in tuple(self._publish_listeners):
            try:
                listener(session_id)
            except Exception:  # A listener's fault must not fail a commit that is already durable
                logger.exception("Snapshot publication listener failed")

    def add_publish_listener(self, listener: Callable[[str], None]) -> None:
        """Calls listener(session_id) on the writer thread after each commit changes what
        published_snapshot_json gives for that session (or ends it). It must return at once."""
        self._publish_listeners = [*self._publish_listeners, listener]

    def remove_publish_listener(self, listener: Callable[[str], None]) -> None:
        # Equality, not identity: each bound method looked up is a new object
        self._publish_listeners = [item for item in self._publish_listeners if item != listener]

    @staticmethod
    def _event(snapshot: FullSnapshot, sequence: int, event_type: str, entity_type: str,
               entity_id: str, entity_revision: int, data: dict) -> Event:
        return Event(event_id=str(uuid4()), event_sequence=sequence, tick=snapshot.tick,
                     entity_type=entity_type, entity_id=entity_id, entity_revision=entity_revision,
                     event_type=event_type, source=snapshot.source, data=data)

    @staticmethod
    def _append_event(connection: sqlite3.Connection, session_id: str, event: Event) -> None:
        sequence, event_id, _, raw = _encode_event(event)
        connection.execute("INSERT INTO events VALUES (?, ?, ?, ?)", (session_id, sequence, event_id, raw))

    def _recover(self, connection: sqlite3.Connection) -> None:
        rows = connection.execute("SELECT * FROM sessions WHERE status NOT IN ('COMPLETED', 'ARCHIVED', 'FAILED')").fetchall()
        for row in rows:
            with self.trace.span("session.recovery", session_id=row["session_id"]) as fields:
                self._publish(row["session_id"], self._recover_session(connection, row, fields))

    @staticmethod
    def _recover_session(connection: sqlite3.Connection, row: sqlite3.Row, fields: dict) -> _EncodedSnapshot:
        snapshot = _load_snapshot(row)
        checkpoint = connection.execute(
            "SELECT state_hash FROM checkpoints WHERE session_id=? ORDER BY tick DESC, event_sequence DESC LIMIT 1",
            (snapshot.session_id,),
        ).fetchone()
        if checkpoint is None or checkpoint[0] != row["state_hash"]:
            raise TrainingStoreError("CORRUPT_CHECKPOINT", "Latest checkpoint does not match session state")
        connection.execute("BEGIN IMMEDIATE")
        queued = connection.execute(
            "SELECT command_id FROM commands WHERE session_id=? AND status='QUEUED' ORDER BY rowid",
            (snapshot.session_id,),
        ).fetchall()
        snapshot.session_epoch += 1
        snapshot.session.session_epoch = snapshot.session_epoch
        # An attempt that has never started must still admit pre-trip approval
        # and START. Only previously active attempts need an explicit RESUME.
        pretrip = snapshot.session.status == "READY"
        if snapshot.session.status == "PAUSED" and snapshot.tick == 0:
            runtime = connection.execute("SELECT checkpoint_json, state_hash FROM session_runtimes WHERE session_id=?",
                                         (snapshot.session_id,)).fetchone()
            if runtime is not None and _hash(runtime["checkpoint_json"]) == runtime["state_hash"]:
                try:
                    state = json.loads(runtime["checkpoint_json"])
                    # Repair pre-trip attempts paused by earlier recovery code.
                    pretrip = (state.get("version") in (3, 4) and state.get("tick") == 0
                               and state.get("workflow", {}).get("domain", {}).get("session", {}).get("status") == "READY")
                except (ValueError, TypeError, AttributeError):
                    # Private actor recovery will fail this attempt separately;
                    # malformed private data must not prevent store startup.
                    pass
        snapshot.session.status = "READY" if pretrip else "PAUSED"
        snapshot.session.aggregate_revision += 1
        snapshot.snapshot_id = str(uuid4())
        for command in queued:
            connection.execute(
                "UPDATE commands SET status='NEEDS_RECONFIRMATION', finished_at_utc=?, failure_code='SESSION_RECOVERING' WHERE command_id=?",
                (_now(), command[0]),
            )
            snapshot.event_sequence += 1
            TrainingStore._append_event(connection, snapshot.session_id,
                TrainingStore._event(snapshot, snapshot.event_sequence, "COMMAND_NEEDS_RECONFIRMATION",
                                     "COMMAND", command[0], snapshot.session.aggregate_revision, {}))
        snapshot.event_sequence += 1
        TrainingStore._append_event(connection, snapshot.session_id,
            TrainingStore._event(snapshot, snapshot.event_sequence, "SESSION_RECOVERED", "SESSION",
                                 snapshot.session_id, snapshot.session.aggregate_revision, {"status": snapshot.session.status}))
        encoded = TrainingStore._write_snapshot(connection, snapshot)
        connection.commit()
        fields.update(**boundary(snapshot), snapshot_id=snapshot.snapshot_id,
                      reconfirmation_count=len(queued), outcome=snapshot.session.status)
        return encoded

    @staticmethod
    def _write_runtime(connection: sqlite3.Connection, session_id: str,
                       state: dict | _EncodedRuntime | None) -> None:
        """Upserts an actor checkpoint, as a dict or already encoded (_EncodedRuntime.of). Its "traffic",
        when it has one ({"tick", "state"}), is kept apart in session_traffic: written only when the
        actor gives one, about once a second, and given back with the rest by runtime."""
        encoded = _EncodedRuntime.of(state)
        if encoded is not None:
            if encoded.traffic is not None:
                connection.execute(
                    "INSERT INTO session_traffic VALUES (?, ?, ?, ?) ON CONFLICT(session_id) "
                    "DO UPDATE SET tick=excluded.tick, traffic_json=excluded.traffic_json, state_hash=excluded.state_hash",
                    (session_id, *encoded.traffic),
                )
            connection.execute(
                "INSERT INTO session_runtimes VALUES (?, ?, ?) ON CONFLICT(session_id) "
                "DO UPDATE SET checkpoint_json=excluded.checkpoint_json, state_hash=excluded.state_hash",
                (session_id, encoded.raw, encoded.digest),
            )

    def creation(self, actor_id: str, key: str, request_hash: str) -> FullSnapshot | None:
        with self._reading() as connection:
            row = connection.execute(
                "SELECT * FROM session_creations WHERE actor_id=? AND idempotency_key=?", (actor_id, key),
            ).fetchone()
            if row is None:
                return None
            if row["request_hash"] != request_hash:
                raise TrainingStoreError("IDEMPOTENCY_CONFLICT", "Creation key was used with different scenario settings")
            return self.snapshot(row["session_id"])

    def list_sessions(self, limit: int = -1, offset: int = 0) -> list[dict]:
        """Read public metadata with live attempts before history, newest first."""
        with self._reading() as connection:
            return self._session_rows(connection, limit, offset)

    def session_page(self, limit: int, offset: int = 0, *, actor_id: str | None = None) -> dict:
        """Read one list page and its whole-table counts from one consistent view."""
        with self._reading() as connection:
            connection.execute("BEGIN")
            visible, parameters = self._visible_sessions(actor_id)
            total = connection.execute("SELECT COUNT(*) FROM sessions s LEFT JOIN session_assessments a USING(session_id)"
                                       + visible, parameters).fetchone()[0]
            live = connection.execute(f"SELECT COALESCE(SUM({_OCCUPIES_CAPACITY}), 0) FROM sessions s").fetchone()[0]
            practice = connection.execute(
                "SELECT COUNT(*) FROM sessions s LEFT JOIN session_assessments a USING(session_id)"
                " WHERE a.session_id IS NULL AND s.status IN ('COMPLETED', 'ARCHIVED', 'FAILED')").fetchone()[0]
            rows = self._session_rows(connection, limit, offset, actor_id=actor_id)
            return {"sessions": rows, "live_count": live, "total": total, "terminal_practice_count": practice}

    def _visible_sessions(self, actor_id):
        """Keep internal listings complete and filter authenticated API listings by assessment access."""
        if actor_id is None or self.assessment_policy.is_supervisor(actor_id):
            return "", ()
        return " WHERE a.session_id IS NULL OR a.trainee_id=?", (actor_id,)

    def _session_rows(self, connection: sqlite3.Connection, limit: int, offset: int,
                      *, actor_id: str | None = None) -> list[dict]:
        visible, parameters = self._visible_sessions(actor_id)
        rows = connection.execute(
            "SELECT s.session_id, s.scenario_revision_id, s.status, a.trainee_id,"
            f" {_OCCUPIES_CAPACITY} AS occupies_capacity FROM sessions s LEFT JOIN session_assessments a USING(session_id)"
            + visible +
            " ORDER BY occupies_capacity DESC, s.rowid DESC LIMIT ? OFFSET ?",
            (*parameters, limit, offset),
        ).fetchall()
        entries = []
        for row in rows:
            entry = dict(row)
            owner = entry.pop("trainee_id")
            entry.update(occupies_capacity=bool(row["occupies_capacity"]), is_assessment=owner is not None,
                         can_archive=bool(row["occupies_capacity"]) and
                         (owner is None or owner == actor_id or self.assessment_policy.is_supervisor(actor_id)),
                         can_delete=row["status"] in _TERMINAL and
                         (owner is None or self.assessment_policy.is_supervisor(actor_id)))
            entries.append(entry)
        return entries

    def expired_session_ids(self, keep_latest: int) -> list[str]:
        """Find old practice attempts. Assessment records are retained."""
        with self._reading() as connection:
            rows = connection.execute(
                "SELECT session_id FROM sessions WHERE status IN ('ARCHIVED', 'COMPLETED', 'FAILED')"
                " AND session_id NOT IN (SELECT session_id FROM session_assessments)"
                " ORDER BY rowid DESC LIMIT -1 OFFSET ?",
                (keep_latest,),
            ).fetchall()
            return [row[0] for row in rows]

    def delete_session(self, session_id: str, *, actor_id: str | None = None) -> None:
        """Permanently remove a terminal attempt, including all of its history.

        Check the persisted lifecycle in the same writer transaction as deletion.
        Missing sessions are a successful retry after an uncertain HTTP response.
        """
        def write(connection: sqlite3.Connection) -> None:
            connection.execute("BEGIN IMMEDIATE")
            row = connection.execute(
                "SELECT s.status, a.trainee_id FROM sessions s LEFT JOIN session_assessments a USING(session_id)"
                " WHERE s.session_id=?", (session_id,)).fetchone()
            if row is not None:
                if row["trainee_id"] is not None and not self.assessment_policy.is_supervisor(actor_id):
                    raise TrainingStoreError("ROLE_DENIED", "Assessment records are retained. Only a configured supervisor can delete them.")
                if row["status"] not in {"ARCHIVED", "COMPLETED", "FAILED"}:
                    raise TrainingStoreError("INVALID_TRANSITION", "Archive the session before permanently deleting it")
                for table in ("session_creations", "session_runtimes", "session_traffic", "commands", "events",
                              "checkpoints", "sessions"):
                    connection.execute(f"DELETE FROM {table} WHERE session_id=?", (session_id,))
            connection.commit()
            self._publish(session_id, None)
        self._submit(write)

    def compact(self) -> dict:
        """Return reusable pages to disk during an explicit maintenance window."""
        def size() -> int:
            return sum(path.stat().st_size for path in (self.path, Path(str(self.path) + "-wal")) if path.exists())

        def write(connection: sqlite3.Connection) -> dict:
            if connection.execute(
                "SELECT 1 FROM sessions WHERE status NOT IN ('ARCHIVED', 'COMPLETED', 'FAILED') LIMIT 1",
            ).fetchone() is not None:
                raise TrainingStoreError("INVALID_TRANSITION", "Archive all sessions before reclaiming disk space")
            before = size()
            # VACUUM is deliberately separate from deletion: it can rewrite a large
            # database and must never stall a running simulation's durable ticks.
            connection.execute("VACUUM")
            result = connection.execute("PRAGMA wal_checkpoint(TRUNCATE)").fetchone()
            return {"database_bytes_before": before, "database_bytes_after": size(), "wal_truncated": result[0] == 0}
        return self._submit(write)

    def runtime_ids(self) -> list[str]:
        with self._reading() as connection:
            rows = connection.execute(
                "SELECT s.session_id FROM sessions s WHERE s.status IN ('READY', 'PAUSED', 'RUNNING')"
                " AND (EXISTS (SELECT 1 FROM session_runtimes r WHERE r.session_id=s.session_id)"
                " OR EXISTS (SELECT 1 FROM session_creations c WHERE c.session_id=s.session_id))",
            ).fetchall()
            return [row[0] for row in rows]

    def traffic_runtime_ids(self) -> set[str]:
        """Sessions with a traffic checkpoint: a hint, before restoring them, of which ones drive traffic."""
        with self._reading() as connection:
            return {row[0] for row in connection.execute("SELECT session_id FROM session_traffic").fetchall()}

    def runtime(self, session_id: str) -> tuple[FullSnapshot, dict]:
        with self._reading() as connection:
            row = connection.execute(
                "SELECT s.*, r.checkpoint_json, r.state_hash AS runtime_hash, t.traffic_json,"
                " t.state_hash AS traffic_hash FROM sessions s LEFT JOIN session_runtimes r USING(session_id)"
                " LEFT JOIN session_traffic t USING(session_id) WHERE s.session_id=?", (session_id,),
            ).fetchone()
            if row is None:
                raise TrainingStoreError("INVALID_REFERENCE", "Training session was not found")
            if row["checkpoint_json"] is None or _hash(row["checkpoint_json"]) != row["runtime_hash"]:
                raise TrainingStoreError("CORRUPT_CHECKPOINT", "Actor checkpoint hash does not match")
            state = json.loads(row["checkpoint_json"])
            if row["traffic_json"] is not None:
                if _hash(row["traffic_json"]) != row["traffic_hash"]:
                    raise TrainingStoreError("CORRUPT_CHECKPOINT", "Traffic checkpoint hash does not match")
                state["traffic"] = json.loads(row["traffic_json"])
            return _load_snapshot(row), state

    def runtimes(self) -> list[tuple[FullSnapshot, dict]]:
        return [self.runtime(session_id) for session_id in self.runtime_ids()]

    def next_session_number(self) -> int:
        """Issue the next running number for a readable session id; never reused, gaps allowed."""
        def write(connection: sqlite3.Connection) -> int:
            connection.execute("BEGIN IMMEDIATE")
            number = connection.execute("INSERT INTO session_numbers (issued_at_utc) VALUES (?)", (_now(),)).lastrowid
            connection.commit()
            return number
        return self._submit(write)

    def create_session(self, snapshot: FullSnapshot, *, runtime_state: dict | None = None,
                       creation: tuple[str, str, str] | None = None,
                       assessment_plan: AssessmentPlan | None = None) -> FullSnapshot:
        if assessment_plan is not None and creation is None:
            raise ValueError("Scored attempts require an authenticated creator")
        snapshot = FullSnapshot.model_validate(snapshot.model_dump(mode="json"))
        runtime = _EncodedRuntime.of(runtime_state)
        def write(connection: sqlite3.Connection) -> FullSnapshot:
            connection.execute("BEGIN IMMEDIATE")
            encoded = self._write_snapshot(connection, snapshot, initial=True)
            self._write_runtime(connection, snapshot.session_id, runtime)
            if creation is not None:
                connection.execute("INSERT INTO session_creations VALUES (?, ?, ?, ?)", (*creation, snapshot.session_id))
            if assessment_plan is not None:
                connection.execute(
                    "INSERT INTO session_assessments (session_id, trainee_id, plan_json, activity_at_utc) VALUES (?, ?, ?, ?)",
                    (snapshot.session_id, creation[0], assessment_plan.model_dump_json(), _now()))
            connection.commit()
            if assessment_plan is not None:
                self._assessment_owners[snapshot.session_id] = creation[0]
            self._publish(snapshot.session_id, encoded)
            return snapshot
        return self._submit(write)

    def _check_assessment_owner(self, trainee_id: str | None, actor_id: str | None, *, supervisor: bool = False) -> None:
        """Use the authenticated owner for scored commands, result access and deletion."""
        if trainee_id is not None and trainee_id != actor_id and not (supervisor and self.assessment_policy.is_supervisor(actor_id)):
            raise TrainingStoreError("ROLE_DENIED", "Only the assigned trainee can access this assessment or change its session")

    def check_read_access(self, session_id: str, actor_id: str) -> None:
        """Authorize private assessment reads without adding database reads to live practice polls."""
        owner = self._assessment_owners.get(session_id)
        if owner is None and session_id not in self._published:
            with self._reading() as connection:
                row = connection.execute("SELECT trainee_id FROM session_assessments WHERE session_id=?", (session_id,)).fetchone()
                owner = row[0] if row else None
        self._check_assessment_owner(owner, actor_id, supervisor=True)

    def inactive_assessments(self, before: str) -> list[dict]:
        """Use persisted owner activity so a server restart cannot prolong abandoned attempts."""
        live_ids = tuple(self._assessment_owners.copy())
        if not live_ids:
            return []
        placeholders = ",".join("?" for _ in live_ids)
        with self._reading() as connection:
            return [dict(row) for row in connection.execute(
                "SELECT a.session_id, a.trainee_id FROM session_assessments a JOIN sessions s USING(session_id)"
                f" WHERE a.session_id IN ({placeholders}) AND s.status IN ('READY', 'PAUSED') AND a.activity_at_utc<=?",
                (*live_ids, before))]

    def assessment_is_inactive(self, session_id: str, before: str) -> bool:
        """Recheck expiry while the runtime holds this session's command lock."""
        with self._reading() as connection:
            return connection.execute(
                "SELECT 1 FROM session_assessments a JOIN sessions s USING(session_id)"
                " WHERE a.session_id=? AND s.status IN ('READY', 'PAUSED') AND a.activity_at_utc<=?",
                (session_id, before)).fetchone() is not None

    def assessment(self, session_id: str, actor_id: str) -> SessionAssessment:
        """Return assessment status or the saved immutable scoring breakdown."""
        with self._reading() as connection:
            record = connection.execute(
                "SELECT s.scenario_revision_id, a.trainee_id, a.plan_json, a.result_json"
                " FROM sessions s LEFT JOIN session_assessments a USING(session_id) WHERE s.session_id=?",
                (session_id,)).fetchone()
            if record is None:
                raise TrainingStoreError("INVALID_REFERENCE", "Training session was not found")
            self._check_assessment_owner(record["trainee_id"], actor_id, supervisor=True)
            if record["result_json"] is not None:
                result = SessionAssessment.model_validate_json(record["result_json"])
                if result.duration_s is None:
                    result.duration_s = json.loads(record["plan_json"])["rubric"]["exercise_limit_s"]
                return result
            rubric = AssessmentPlan.model_validate_json(record["plan_json"]).rubric if record["plan_json"] is not None else None
            return SessionAssessment(session_id=session_id, scenario_revision_id=record["scenario_revision_id"],
                rubric_revision=rubric.revision if rubric else None, points=None, maximum=100 if rubric else None,
                duration_s=rubric.exercise_limit_s if rubric else None,
                pass_mark=rubric.pass_mark if rubric else None, through_tick=None, through_event_sequence=None,
                status="IN_PROGRESS" if rubric else "PRACTICE",
                explanation=("The assessment is saved when this exercise ends." if rubric else "This is a practice session."))

    def prepare_assessment(self, snapshot: FullSnapshot, events: tuple[Event, ...]) -> PreparedAssessment | None:
        """Read one consistent journal boundary and run all evaluators on the caller's thread."""
        if snapshot.session.status not in _TERMINAL:
            return
        with self._reading() as connection:
            connection.execute("BEGIN")
            row = connection.execute("SELECT plan_json FROM session_assessments WHERE session_id=? AND result_json IS NULL",
                                     (snapshot.session_id,)).fetchone()
            if row is None:
                return
            previous = self._stored_boundary(connection, snapshot.session_id)
            if snapshot.event_sequence != previous["event_sequence"] + len(events):
                return  # The writer will apply its ordinary stale-boundary checks.
            journal = [item[0] for item in connection.execute(
                "SELECT event_json FROM events WHERE session_id=? AND event_sequence<=? ORDER BY event_sequence",
                (snapshot.session_id, previous["event_sequence"]))]
        grade = finalize(AssessmentPlan.model_validate_json(row["plan_json"]), snapshot,
                         chain((Event.model_validate_json(raw) for raw in journal), events))
        return PreparedAssessment(previous["snapshot_id"], row["plan_json"], grade.model_dump(mode="json"))

    @staticmethod
    def _save_assessment(connection: sqlite3.Connection, snapshot: FullSnapshot,
                         prepared: PreparedAssessment | None, previous_snapshot_id: str) -> None:
        """Verify and save a prepared grade atomically with the terminal boundary."""
        row = connection.execute(
            "SELECT plan_json FROM session_assessments WHERE session_id=? AND result_json IS NULL",
            (snapshot.session_id,)).fetchone()
        if row is None:
            return
        if prepared is None or prepared.snapshot_id != previous_snapshot_id or prepared.plan_json != row["plan_json"]:
            raise TrainingStoreError("STALE_REVISION", "Assessment evidence changed before the terminal commit")
        # Expired command receipts are appended by the writer after preparation.
        report = {**prepared.result, "through_tick": snapshot.tick, "through_event_sequence": snapshot.event_sequence}
        connection.execute("UPDATE session_assessments SET result_json=? WHERE session_id=? AND result_json IS NULL",
                           (_json(report), snapshot.session_id))

    @staticmethod
    def _expire_pending(connection: sqlite3.Connection, snapshot: FullSnapshot, *, except_id: str = "") -> None:
        """Settle accepted receipts before retiring their runtime."""
        pending = connection.execute(
            "SELECT command_id, target_revision FROM commands"
            " WHERE session_id=? AND status='QUEUED' AND command_id<>? ORDER BY rowid",
            (snapshot.session_id, except_id),
        ).fetchall()
        for waiting in pending:
            snapshot.event_sequence += 1
            event = TrainingStore._event(snapshot, snapshot.event_sequence, "COMMAND_EXPIRED", "COMMAND",
                waiting["command_id"], waiting["target_revision"],
                {"status": "EXPIRED", "failure_code": "INVALID_TRANSITION"})
            TrainingStore._append_event(connection, snapshot.session_id, event)
            connection.execute(
                "UPDATE commands SET status='EXPIRED', finished_at_utc=?, result_event_id=?,"
                " failure_code='INVALID_TRANSITION' WHERE command_id=?",
                (_now(), event.event_id, waiting["command_id"]),
            )

    def _read_connection(self, *, check_same_thread: bool = True) -> sqlite3.Connection:
        connection = sqlite3.connect(f"file:{self.path.as_posix()}?mode=ro", uri=True,
                                     check_same_thread=check_same_thread)
        connection.row_factory = sqlite3.Row
        return connection

    @contextmanager
    def _reading(self) -> Iterator[sqlite3.Connection]:
        """This thread's read-only connection, for one read.

        Kept open between reads: opening one costs the database and its write-ahead log being opened
        again, which right after a commit took some 20 ms (against well under one for the query), and a
        live session reads at its publications. Each read sees the latest commit, as on a new
        connection: a read transaction it began is ended when it is done, and no statement is left
        running. A store that is closed opens one for the read alone.
        """
        connection = getattr(self._readers, "connection", None)
        if connection is None:
            if self._lock_file.closed:
                with closing(self._read_connection()) as connection:
                    yield connection
                return
            connection = self._read_connection(check_same_thread=False)
            with self._readers_lock:
                self._reader_connections.append(connection)
            self._readers.connection = connection
        try:
            yield connection
        finally:
            if connection.in_transaction:
                connection.rollback()

    def snapshot(self, session_id: str) -> FullSnapshot:
        """The session's latest committed snapshot, the caller's own to change."""
        raw = self._published.get(session_id)
        if raw is not None:
            return FullSnapshot.model_validate_json(raw)
        with self._reading() as connection:
            row = connection.execute("SELECT * FROM sessions WHERE session_id=?", (session_id,)).fetchone()
            if row is None:
                raise TrainingStoreError("INVALID_REFERENCE", "Training session was not found")
            return _load_snapshot(row)

    def published_snapshot_json(self, session_id: str) -> str | None:
        """The latest committed snapshot of a session not yet ended, as JSON, from memory: None when the
        store holds none for it (snapshot_json then reads the database). Never blocks."""
        return self._published.get(session_id)

    def snapshot_json(self, session_id: str) -> str:
        """The session's latest committed snapshot as the JSON GET /snapshot sends.

        A live session's comes from memory, as committed. Any other's is read and parsed: a row written
        by an older backend can need the contract's upgrades on the way out."""
        raw = self._published.get(session_id)
        if raw is not None:
            return raw
        return _snapshot_json(self.snapshot(session_id))

    @staticmethod
    def _status(row: sqlite3.Row, snapshot: FullSnapshot) -> CommandStatus:
        return CommandStatus(
            contract_version="2.0", message_type="COMMAND_STATUS", session_id=snapshot.session_id,
            session_epoch=snapshot.session_epoch, scene_revision=snapshot.scene_revision,
            transform_revision=snapshot.transform_revision, tick=snapshot.tick,
            simulation_time_s=snapshot.simulation_time_s, event_sequence=snapshot.event_sequence,
            source=snapshot.source, command_id=row["command_id"], idempotency_key=row["idempotency_key"],
            status=row["status"], target_type=row["target_type"], target_id=row["target_id"],
            target_revision=row["target_revision"], applied_tick=row["applied_tick"],
            result_event_id=row["result_event_id"], failure_code=row["failure_code"],
        )

    def command_record(self, session_id: str, actor_id: str, key: str) -> tuple[CommandStatus, DispatcherCommand]:
        with self._reading() as connection:
            connection.execute("BEGIN")
            session = connection.execute("SELECT * FROM sessions WHERE session_id=?", (session_id,)).fetchone()
            row = connection.execute(
                "SELECT * FROM commands WHERE session_id=? AND actor_id=? AND idempotency_key=?",
                (session_id, actor_id, key),
            ).fetchone()
            if session is None or row is None:
                raise TrainingStoreError("INVALID_REFERENCE", "Command was not found")
            return self._status(row, _load_snapshot(session)), DispatcherCommand.model_validate_json(row["command_json"])

    def command_status(self, session_id: str, actor_id: str, key: str) -> CommandStatus:
        return self.command_record(session_id, actor_id, key)[0]

    def accept(self, actor_id: str, command: DispatcherCommand, *, defer_preconditions: bool = False) -> CommandStatus:
        with correlate(command_id=command.command_id, submitted_command_id=command.command_id,
                       command_type=command.command_type, session_id=command.session_id,
                       session_epoch=command.expected_epoch):
            with self.trace.span("command.accept") as fields:
                result = self._accept(actor_id, command, defer_preconditions=defer_preconditions)
                fields.update(**boundary(result), command_id=result.command_id, outcome=result.status,
                              failure_code=result.failure_code)
                return result

    def _accept(self, actor_id: str, command: DispatcherCommand, *, defer_preconditions: bool = False) -> CommandStatus:
        """Journal an input. Domain actors may defer preconditions to ``apply``
        so denied incident attempts also receive one durable command outcome.
        """
        if not actor_id:
            raise TrainingStoreError("ROLE_DENIED", "Authenticated actor is required")
        command = DispatcherCommand.model_validate(command.model_dump(mode="json"))
        expected_target = {
            "START": "SESSION", "PAUSE": "SESSION", "RESUME": "SESSION", "RESET": "SESSION", "ARCHIVE": "SESSION",
            "APPROVE_ROUTE": "ROUTE", "APPROVE_INCIDENT_OPTION": "INCIDENT",
            "RESERVE_CHARGER": "CHARGER", "CONTINUE_SERVICE": "VEHICLE",
            "SCHEDULE_CHARGE": "CHARGER", "CANCEL_CHARGE_BOOKING": "CHARGER",
        }[command.command_type]
        if command.target_type != expected_target:
            raise TrainingStoreError("INVALID_PAYLOAD", "Command type and target type do not match")
        payload_hash = _hash(_json(command.model_dump(mode="json", exclude={"command_id", "idempotency_key"})))
        def write(connection: sqlite3.Connection) -> CommandStatus:
            connection.execute("BEGIN IMMEDIATE")
            session = connection.execute(
                "SELECT s.*, a.trainee_id FROM sessions s LEFT JOIN session_assessments a USING(session_id)"
                " WHERE s.session_id=?", (command.session_id,)).fetchone()
            if session is None:
                raise TrainingStoreError("INVALID_REFERENCE", "Training session was not found")
            self._check_assessment_owner(session["trainee_id"], actor_id, supervisor=command.command_type == "ARCHIVE")
            snapshot = _load_snapshot(session)
            prior = connection.execute(
                "SELECT * FROM commands WHERE actor_id=? AND session_id=? AND idempotency_key=?",
                (actor_id, command.session_id, command.idempotency_key),
            ).fetchone()
            if prior is not None:
                if prior["payload_hash"] != payload_hash:
                    raise TrainingStoreError("IDEMPOTENCY_CONFLICT", "Idempotency key was used with different command content")
                connection.commit()
                return self._status(prior, snapshot)
            if snapshot.session.status in {"RECOVERING", "COMPLETED", "ARCHIVED", "FAILED"}:
                if snapshot.session.status in _TERMINAL and (command.command_type == "ARCHIVE" or session["trainee_id"] is not None):
                    raise TrainingStoreError("INVALID_TRANSITION", "This attempt has already ended. View the saved result in Reports.")
                raise TrainingStoreError("SESSION_RECOVERING", "Session does not accept commands")
            expected_status = {"START": "READY", "PAUSE": "RUNNING", "RESUME": "PAUSED"}.get(command.command_type)
            if expected_status is not None and snapshot.session.status != expected_status:
                raise TrainingStoreError("INVALID_TRANSITION", "Session lifecycle precondition failed")
            failure = validate_command_against_snapshot(command, snapshot)
            if failure is not None and not defer_preconditions:
                raise TrainingStoreError(failure.code, failure.message, current_revision=failure.current_revision)
            target_revision = _target_revision(snapshot, command.target_type, command.target_id)
            try:
                connection.execute(
                    "INSERT INTO commands (command_id, session_id, actor_id, idempotency_key, payload_hash, command_json, reason, expected_epoch, expected_revision, target_type, target_id, target_revision, status, accepted_at_utc) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'QUEUED', ?)",
                    (command.command_id, command.session_id, actor_id, command.idempotency_key,
                     payload_hash, _json(command.model_dump(mode="json")), command.reason,
                     command.expected_epoch, command.expected_revision, command.target_type,
                     command.target_id, target_revision, _now()),
                )
            except sqlite3.IntegrityError as error:
                raise TrainingStoreError("IDEMPOTENCY_CONFLICT", "Command ID is already in use") from error
            row = connection.execute("SELECT * FROM commands WHERE command_id=?", (command.command_id,)).fetchone()
            connection.commit()
            return self._status(row, snapshot)
        return self._submit(write)

    def apply(self, command_id: str, proposed: FullSnapshot | None = None,
              domain_events: tuple[Event, ...] = (), *, rejection_code: str | None = None,
              expected_snapshot_id: str | None = None, runtime_state: dict | None = None) -> CommandStatus:
        with correlate(command_id=command_id, **(boundary(proposed) if proposed is not None else {})):
            with self.trace.span("command.apply") as fields:
                result = self._apply(command_id, proposed, domain_events, rejection_code=rejection_code,
                                     expected_snapshot_id=expected_snapshot_id, runtime_state=runtime_state,
                                     trace_fields=fields)
                fields.update(**boundary(result), outcome=result.status, failure_code=result.failure_code,
                              result_event_id=result.result_event_id)
                return result

    def _apply(self, command_id: str, proposed: FullSnapshot | None = None,
               domain_events: tuple[Event, ...] = (), *, rejection_code: str | None = None,
               expected_snapshot_id: str | None = None, runtime_state: dict | None = None,
               trace_fields: dict, published: dict | None = None,
               prepared_assessment: PreparedAssessment | None = None) -> CommandStatus:
        """Commit one actor-staged boundary, rechecking preconditions in the writer.

        On a stale command or staged boundary, the proposed state is discarded
        and a durable REJECTED outcome is written. A valid command requires a
        revised target snapshot. An actor may supply rejection_code when its
        own revalidation finds a changed constraint after acceptance.

        published, when given, gets "raw": what this commit published for the session (as
        published_snapshot_json gave just after it, on the writer, before any later commit), or is
        left empty when the command was already settled and nothing was written.
        """
        if proposed is not None:
            proposed = FullSnapshot.model_validate(proposed.model_dump(mode="json"))
        domain_events = tuple(Event.model_validate(event.model_dump(mode="json")) for event in domain_events)
        runtime = _EncodedRuntime.of(runtime_state)
        prepared = prepared_assessment
        if prepared is None and proposed is not None:
            prepared = self.prepare_assessment(proposed, domain_events)
        def write(connection: sqlite3.Connection) -> CommandStatus:
            connection.execute("BEGIN IMMEDIATE")
            row = connection.execute("SELECT * FROM commands WHERE command_id=?", (command_id,)).fetchone()
            if row is None:
                raise TrainingStoreError("INVALID_REFERENCE", "Command was not found")
            session = connection.execute("SELECT * FROM sessions WHERE session_id=?", (row["session_id"],)).fetchone()
            snapshot = _load_snapshot(session)
            command = DispatcherCommand.model_validate_json(row["command_json"])
            trace_fields.update(**boundary(snapshot), command_type=command.command_type)
            if row["status"] != "QUEUED":
                connection.commit()
                return self._status(row, snapshot)
            failure = validate_command_against_snapshot(command, snapshot)
            if failure is None and expected_snapshot_id is not None and snapshot.snapshot_id != expected_snapshot_id:
                failure = ValidationFailure(code="STALE_REVISION", message="Committed checkpoint changed",
                                            retryable=True)
            if failure is None and rejection_code is not None:
                failure = ValidationFailure(code=rejection_code, message="Command revalidation failed", retryable=False)
            expected_status = {"START": "READY", "PAUSE": "RUNNING", "RESUME": "PAUSED"}.get(command.command_type)
            if failure is None and expected_status is not None and snapshot.session.status != expected_status:
                failure = ValidationFailure(code="INVALID_TRANSITION", message="Session lifecycle precondition changed",
                                            retryable=False)
            if failure is None and proposed is None:
                raise TrainingStoreError("INVALID_PAYLOAD", "Actor must supply the staged snapshot")
            if failure is None and proposed.session_id != snapshot.session_id:
                raise TrainingStoreError("INVALID_PAYLOAD", "Staged snapshot targets a different session")
            if failure is None and (proposed.session_epoch != snapshot.session_epoch or
                                    proposed.event_sequence != snapshot.event_sequence + len(domain_events) or
                                    proposed.tick < snapshot.tick or
                                    proposed.scene_revision != snapshot.scene_revision or
                                    proposed.spatial != snapshot.spatial or
                                    proposed.transform_revision != snapshot.transform_revision or
                                    proposed.session.scenario_revision_id != snapshot.session.scenario_revision_id):
                failure = ValidationFailure(code="STALE_REVISION", message="Staged snapshot does not match committed boundary",
                                            retryable=False)
            if failure is None:
                old_revision = _target_revision(snapshot, command.target_type, command.target_id)
                new_revision = _target_revision(proposed, command.target_type, command.target_id)
                if new_revision <= old_revision:
                    raise TrainingStoreError("INVALID_PAYLOAD", "Applied command must advance its target revision")
                if command.command_type == "APPROVE_ROUTE":
                    before_route = next(r for r in snapshot.routes if r.route_id == command.target_id)
                    after_route = next(r for r in proposed.routes if r.route_id == command.target_id)
                    targets = set(command.payload["target_vehicle_ids"])
                    if (after_route.revision != before_route.revision + 1 or
                        after_route.directed_edge_ids != command.payload["directed_edge_ids"] or
                        after_route.required_stop_ids != before_route.required_stop_ids or
                        {v.vehicle_id for v in proposed.vehicles if v.route_id == command.target_id and v.route_revision == after_route.revision} != targets):
                        raise TrainingStoreError("INVALID_PAYLOAD", "Staged route approval differs from accepted command")
                if (proposed.session.status != snapshot.session.status and not
                    transition_allowed("session", snapshot.session.status, proposed.session.status)):
                    raise TrainingStoreError("INVALID_TRANSITION", "Invalid session lifecycle transition")
                self._check_events(domain_events, snapshot.event_sequence, proposed.tick)
                result = proposed
                status, code = "APPLIED", None
                target_revision = new_revision
            else:
                result = snapshot.model_copy(deep=True)
                status, code = "REJECTED", failure.code
                target_revision = _target_revision(snapshot, command.target_type, command.target_id)
            for domain_event in domain_events if status == "APPLIED" else ():
                self._append_event(connection, result.session_id, domain_event)
            if status == "APPLIED" and command.command_type == "ARCHIVE":
                self._expire_pending(connection, result, except_id=command_id)
            result.event_sequence += 1
            result.snapshot_id = str(uuid4())
            event = self._event(result, result.event_sequence, f"COMMAND_{status}", "COMMAND",
                                command_id, target_revision, {"status": status, "failure_code": code})
            self._append_event(connection, result.session_id, event)
            encoded = self._write_snapshot(connection, result)
            if status == "APPLIED":
                self._write_runtime(connection, result.session_id, runtime)
                if result.session_id in self._assessment_owners:
                    connection.execute("UPDATE session_assessments SET activity_at_utc=? WHERE session_id=?",
                                       (_now(), result.session_id))
            connection.execute(
                "UPDATE commands SET status=?, finished_at_utc=?, applied_tick=?, result_event_id=?, failure_code=?, target_revision=? WHERE command_id=?",
                (status, _now(), result.tick if status == "APPLIED" else None,
                 event.event_id, code, target_revision, command_id),
            )
            if status == "APPLIED" and result.session.status in {"COMPLETED", "ARCHIVED", "FAILED"}:
                self._save_assessment(connection, result, prepared, snapshot.snapshot_id)
            row = connection.execute("SELECT * FROM commands WHERE command_id=?", (command_id,)).fetchone()
            connection.commit()
            self._publish(result.session_id, encoded)
            if published is not None:
                published["raw"] = self._published.get(result.session_id)
            return self._status(row, result)
        return self._submit(write)

    @staticmethod
    def _check_events(events: tuple[Event, ...], after: int, tick: int) -> None:
        TrainingStore._check_event_order([(event.event_sequence, event.tick) for event in events], after, tick)

    @staticmethod
    def _check_event_order(events: list[tuple[int, int]], after: int, tick: int) -> None:
        """events as (sequence, tick): they must follow after without a gap, all at tick."""
        if [sequence for sequence, _ in events] != list(range(after + 1, after + 1 + len(events))):
            raise TrainingStoreError("STALE_REVISION", "Domain event sequence has a gap")
        if any(event_tick != tick for _, event_tick in events):
            raise TrainingStoreError("INVALID_PAYLOAD", "Domain events must belong to the staged tick")

    def commit_snapshot(self, snapshot: FullSnapshot, domain_events: tuple[Event, ...] = (), *,
                        expected_snapshot_id: str | None = None, runtime_state: dict | None = None) -> FullSnapshot:
        with correlate(**boundary(snapshot)):
            with self.trace.span("snapshot.commit") as fields:
                result = self._commit_snapshot(snapshot, domain_events, expected_snapshot_id=expected_snapshot_id,
                                               runtime_state=runtime_state)
                fields.update(snapshot_id=result.snapshot_id, **boundary(result))
                return result

    def _commit_snapshot(self, snapshot: FullSnapshot, domain_events: tuple[Event, ...] = (), *,
                         expected_snapshot_id: str | None = None, runtime_state: dict | None = None) -> FullSnapshot:
        """Persist an actor publication and its ordered events before visibility.

        The caller gives up snapshot: once committed it is returned, with its new snapshot ID, as the
        committed snapshot, and must not be changed after. It is serialised once, here on the caller's
        thread, and the writer checks it against the stored boundary (boundary_json) without parsing the
        stored snapshot. A refused or failed commit leaves it as it was.
        """
        commit = self.encode_commit(snapshot, domain_events, expected_snapshot_id=expected_snapshot_id,
                                    runtime_state=runtime_state)
        self.submit_commit(commit).result()
        if commit.snapshot.status in _TERMINAL:
            return self.snapshot(snapshot.session_id)
        snapshot.snapshot_id = commit.snapshot_id
        return snapshot

    def encode_commit(self, snapshot: FullSnapshot, domain_events: tuple[Event, ...] = (), *,
                      expected_snapshot_id: str | None = None, runtime_state: dict | None = None) -> EncodedCommit:
        """The caller's half of commit_snapshot: validates the events and serialises the publication
        under a new snapshot ID, leaving snapshot as it was. Needs no writer, so a worker process can do it."""
        validated = tuple(Event.model_validate(event.model_dump(mode="json")) for event in domain_events)
        prepared = self.prepare_assessment(snapshot, validated)
        events = tuple(_encode_event(event) for event in validated)
        snapshot_id = str(uuid4())
        previous_id, snapshot.snapshot_id = snapshot.snapshot_id, snapshot_id
        try:
            encoded = _EncodedSnapshot.of(snapshot)
        finally:
            snapshot.snapshot_id = previous_id
        if self.validate_publications:
            FullSnapshot.model_validate_json(encoded.raw)
        return EncodedCommit(snapshot.session_id, snapshot_id, encoded, events, _EncodedRuntime.of(runtime_state),
                             expected_snapshot_id, prepared)

    def submit_commit(self, commit: EncodedCommit) -> Future[None]:
        """The writer's half of commit_snapshot: queues the encoded publication and returns at once. The
        future completes once it is durable (and published for reads), or fails, as with STALE_REVISION
        when it no longer follows the committed boundary. Active publications use the encoded data.
        Terminal publications also settle pending receipts and freeze any assessment in this transaction."""
        proposed = commit.snapshot.boundary
        session_id = commit.session_id
        def write(connection: sqlite3.Connection) -> None:
            connection.execute("BEGIN IMMEDIATE")
            previous = self._stored_boundary(connection, session_id)
            if ((commit.expected_snapshot_id is not None and previous["snapshot_id"] != commit.expected_snapshot_id) or
                proposed["session_epoch"] != previous["session_epoch"] or
                proposed["event_sequence"] != previous["event_sequence"] + len(commit.events) or
                proposed["tick"] < previous["tick"] or
                (proposed["tick"] == previous["tick"] and not commit.events) or
                proposed["scene_revision"] != previous["scene_revision"] or
                proposed["spatial"] != previous["spatial"] or
                proposed["transform_revision"] != previous["transform_revision"] or
                proposed["scenario_revision_id"] != previous["scenario_revision_id"]):
                raise TrainingStoreError("STALE_REVISION", "Publication does not follow the committed boundary")
            self._check_event_order([(sequence, tick) for sequence, _, tick, _ in commit.events],
                                    previous["event_sequence"], proposed["tick"])
            for sequence, event_id, _, raw in commit.events:
                connection.execute("INSERT INTO events VALUES (?, ?, ?, ?)", (session_id, sequence, event_id, raw))
            encoded = commit.snapshot
            if encoded.status in _TERMINAL:
                snapshot = FullSnapshot.model_validate_json(encoded.raw)
                if encoded.status == "COMPLETED":
                    self._expire_pending(connection, snapshot)
                    encoded = _EncodedSnapshot.of(snapshot)
                self._save_assessment(connection, snapshot, commit.assessment, previous["snapshot_id"])
            self._write_encoded(connection, session_id, encoded)
            self._write_runtime(connection, session_id, commit.runtime)
            connection.commit()
            self._publish(session_id, encoded)
        return self._submit_async(write)

    def recover_session(self, session_id: str) -> None:
        """Recovers one session as opening the store recovers each (_recover_session): a new epoch, the
        session paused (or still READY), queued commands marked for reconfirmation. For a session whose
        worker process was lost: its actor goes back to the last committed checkpoint, so clients'
        commands against the old epoch are refused rather than applied to state that no longer exists.
        A terminal or missing session is left alone."""
        def write(connection: sqlite3.Connection) -> None:
            row = connection.execute("SELECT * FROM sessions WHERE session_id=?", (session_id,)).fetchone()
            if row is None or row["status"] in _TERMINAL:
                return
            with self.trace.span("session.recovery", session_id=session_id) as fields:
                self._publish(session_id, self._recover_session(connection, row, fields))
        self._submit(write)

    @staticmethod
    def _stored_boundary(connection: sqlite3.Connection, session_id: str) -> dict:
        """The committed boundary of a session (_boundary), from its small boundary_json when that was
        written with the stored snapshot, else from the snapshot itself."""
        row = connection.execute("SELECT boundary_json, state_hash FROM sessions WHERE session_id=?",
                                 (session_id,)).fetchone()
        if row is None:
            raise TrainingStoreError("INVALID_REFERENCE", "Training session was not found")
        if row["boundary_json"] is not None:
            stored = json.loads(row["boundary_json"])
            if stored.pop("state_hash", None) == row["state_hash"]:
                return stored
        # Written before boundary_json, or the row was changed without it
        row = connection.execute("SELECT * FROM sessions WHERE session_id=?", (session_id,)).fetchone()
        return _boundary(_load_snapshot(row))

    def history(self, session_id: str, start_tick: int, end_tick: int) -> list[FullSnapshot]:
        """One committed checkpoint per tick, bounded to ten seconds at 20 Hz."""
        if start_tick < 0 or end_tick < start_tick or end_tick - start_tick > 200:
            raise TrainingStoreError("INVALID_PAYLOAD", "History window must be at most 200 ticks")
        with self._reading() as connection:
            rows = connection.execute(
                "SELECT c.* FROM checkpoints c JOIN ("
                " SELECT tick, MAX(event_sequence) AS sequence FROM checkpoints"
                " WHERE session_id=? AND tick BETWEEN ? AND ? GROUP BY tick"
                ") latest ON c.tick=latest.tick AND c.event_sequence=latest.sequence"
                " WHERE c.session_id=? ORDER BY c.tick",
                (session_id, start_tick, end_tick, session_id),
            ).fetchall()
            return [_load_snapshot(row) for row in rows]

    def entity_events(self, session_id: str, entity_id: str, through_sequence: int) -> list[Event]:
        """Read durable incident evidence without the event-replay page limit."""
        with self._reading() as connection:
            rows = connection.execute(
                "SELECT event_json FROM events WHERE session_id=? AND event_sequence<=?"
                " AND json_extract(event_json, '$.entity_id')=? ORDER BY event_sequence",
                (session_id, through_sequence, entity_id),
            ).fetchall()
            return [Event.model_validate_json(row[0]) for row in rows]

    def incident_attempts(self, session_id: str, incident_id: str) -> list[dict]:
        with self._reading() as connection:
            rows = connection.execute(
                "SELECT command_id, command_json, status, failure_code, accepted_at_utc, applied_tick"
                " FROM commands WHERE session_id=? AND target_type='INCIDENT' AND target_id=? ORDER BY rowid",
                (session_id, incident_id),
            ).fetchall()
            attempts = []
            for row in rows:
                attempt = dict(row)
                attempt["command"] = json.loads(attempt.pop("command_json"))
                attempts.append(attempt)
            return attempts

    def rejected_commands(self, session_id: str, command_types: set[str], since_tick: int) -> list[DispatcherCommand]:
        """Commands of these types rejected at or after since_tick, oldest first."""
        with self._reading() as connection:
            rows = connection.execute(
                "SELECT c.command_json FROM commands c JOIN events e ON e.event_id = c.result_event_id"
                " WHERE c.session_id=? AND c.status='REJECTED'"
                " AND json_extract(e.event_json, '$.tick')>=? ORDER BY c.rowid",
                (session_id, since_tick),
            ).fetchall()
            commands = [DispatcherCommand.model_validate_json(row[0]) for row in rows]
            return [command for command in commands if command.command_type in command_types]

    def replay(self, session_id: str, after: int, limit: int = 100) -> EventReplay:
        if not 1 <= limit <= 500:
            raise TrainingStoreError("INVALID_PAYLOAD", "Event page limit must be between 1 and 500")
        with self._reading() as connection:
            connection.execute("BEGIN")
            row = connection.execute("SELECT * FROM sessions WHERE session_id=?", (session_id,)).fetchone()
            if row is None:
                raise TrainingStoreError("INVALID_REFERENCE", "Training session was not found")
            snapshot = _load_snapshot(row)
            if after < row["base_sequence"] or after > snapshot.event_sequence:
                raise TrainingStoreError("STALE_REVISION", "Cursor is outside retained event history",
                                         current_revision=snapshot.event_sequence)
            rows = connection.execute(
                "SELECT event_json FROM events WHERE session_id=? AND event_sequence>? ORDER BY event_sequence LIMIT ?",
                (session_id, after, limit + 1),
            ).fetchall()
            has_more = len(rows) > limit
            events = [Event.model_validate_json(item[0]) for item in rows[:limit]]
            return EventReplay(
                contract_version="2.0", message_type="EVENT_REPLAY", session_id=session_id,
                session_epoch=snapshot.session_epoch, scene_revision=snapshot.scene_revision,
                transform_revision=snapshot.transform_revision, tick=snapshot.tick,
                simulation_time_s=snapshot.simulation_time_s, event_sequence=snapshot.event_sequence,
                source=snapshot.source, after_sequence=after, events=events,
                next_sequence=events[-1].event_sequence if events else after, has_more=has_more,
            )
