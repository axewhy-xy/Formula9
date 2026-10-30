"""Single-writer SQLite journal for v2 training sessions.

The session actor stages a new snapshot and calls ``apply`` at a logical step.
The store publishes nothing until its transaction has committed. This module
does not run a simulation or infer domain effects from command payloads.
"""

from __future__ import annotations

import hashlib
import json
import os
import queue
import sqlite3
from concurrent.futures import Future
from contextlib import closing
from contextvars import copy_context
from datetime import UTC, datetime
from pathlib import Path
from threading import Thread
from typing import BinaryIO, Callable, TypeVar
from uuid import uuid4

from src.core.training_telemetry import TrainingTrace, boundary, correlate

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
_MIGRATIONS = Path(__file__).with_name("migrations")


class TrainingStoreError(Exception):
    def __init__(self, code: str, message: str, *, current_revision: int | None = None):
        super().__init__(message)
        self.code = code
        self.current_revision = current_revision


def _json(value: object) -> str:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def _snapshot_json(snapshot: FullSnapshot) -> str:
    return _json(snapshot.model_dump(mode="json"))


def _hash(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


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
    """Bounded synchronous command interface backed by one SQLite writer thread."""

    def __init__(self, path: Path, *, queue_size: int = 128, trace: TrainingTrace | None = None):
        if queue_size < 1:
            raise ValueError("queue_size must be positive")
        self.trace = trace if trace is not None else TrainingTrace()
        self.path = Path(path).resolve()
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._lock_file = _lock_database(self.path)
        self._jobs: queue.Queue = queue.Queue(maxsize=queue_size)
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
            connection.execute("PRAGMA synchronous=FULL")
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
        return future.result()

    def close(self) -> None:
        if self._thread.is_alive():
            self._jobs.put(None)
            self._thread.join()
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
        if current > len(migrations):
            raise TrainingStoreError("SCHEMA_TOO_NEW", "Training database schema is newer than this backend")
        for migration in migrations:
            version = int(migration.name[:4])
            if version <= current:
                continue
            script = migration.read_text(encoding="utf-8")
            connection.executescript("BEGIN IMMEDIATE;\n" + script + f"\nINSERT INTO schema_migrations VALUES ({version}, '{_now()}');\nCOMMIT;")
        self._recover(connection)

    @staticmethod
    def _write_snapshot(connection: sqlite3.Connection, snapshot: FullSnapshot, *, initial: bool = False) -> None:
        raw = _snapshot_json(snapshot)
        digest = _hash(raw)
        if initial:
            connection.execute(
                "INSERT INTO sessions VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                (snapshot.session_id, snapshot.session.scenario_revision_id, snapshot.session_epoch,
                 snapshot.session.status, snapshot.tick, snapshot.event_sequence, snapshot.event_sequence, raw, digest),
            )
        else:
            connection.execute(
                "UPDATE sessions SET epoch=?, status=?, tick=?, event_sequence=?, snapshot_json=?, state_hash=? WHERE session_id=?",
                (snapshot.session_epoch, snapshot.session.status, snapshot.tick, snapshot.event_sequence,
                 raw, digest, snapshot.session_id),
            )
        connection.execute(
            "INSERT INTO checkpoints VALUES (?, ?, ?, ?, ?, ?)",
            (snapshot.session_id, snapshot.event_sequence, snapshot.tick, snapshot.session_epoch, raw, digest),
        )

    @staticmethod
    def _event(snapshot: FullSnapshot, sequence: int, event_type: str, entity_type: str,
               entity_id: str, entity_revision: int, data: dict) -> Event:
        return Event(event_id=str(uuid4()), event_sequence=sequence, tick=snapshot.tick,
                     entity_type=entity_type, entity_id=entity_id, entity_revision=entity_revision,
                     event_type=event_type, source=snapshot.source, data=data)

    @staticmethod
    def _append_event(connection: sqlite3.Connection, session_id: str, event: Event) -> None:
        connection.execute("INSERT INTO events VALUES (?, ?, ?, ?)",
                           (session_id, event.event_sequence, event.event_id, _json(event.model_dump(mode="json"))))

    def _recover(self, connection: sqlite3.Connection) -> None:
        rows = connection.execute("SELECT * FROM sessions WHERE status NOT IN ('COMPLETED', 'ARCHIVED', 'FAILED')").fetchall()
        for row in rows:
            with self.trace.span("session.recovery", session_id=row["session_id"]) as fields:
                self._recover_session(connection, row, fields)

    @staticmethod
    def _recover_session(connection: sqlite3.Connection, row: sqlite3.Row, fields: dict) -> None:
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
                    pretrip = (state.get("version") == 3 and state.get("tick") == 0
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
        TrainingStore._write_snapshot(connection, snapshot)
        connection.commit()
        fields.update(**boundary(snapshot), snapshot_id=snapshot.snapshot_id,
                      reconfirmation_count=len(queued), outcome=snapshot.session.status)

    @staticmethod
    def _write_runtime(connection: sqlite3.Connection, session_id: str, state: dict | None) -> None:
        if state is not None:
            raw = _json(state)
            connection.execute(
                "INSERT INTO session_runtimes VALUES (?, ?, ?) ON CONFLICT(session_id) "
                "DO UPDATE SET checkpoint_json=excluded.checkpoint_json, state_hash=excluded.state_hash",
                (session_id, raw, _hash(raw)),
            )

    def creation(self, actor_id: str, key: str, request_hash: str) -> FullSnapshot | None:
        with closing(self._read_connection()) as connection:
            row = connection.execute(
                "SELECT * FROM session_creations WHERE actor_id=? AND idempotency_key=?", (actor_id, key),
            ).fetchone()
            if row is None:
                return None
            if row["request_hash"] != request_hash:
                raise TrainingStoreError("IDEMPOTENCY_CONFLICT", "Creation key was used with different scenario settings")
            return self.snapshot(row["session_id"])

    def list_sessions(self) -> list[dict]:
        """Read public metadata with live attempts before history, newest first."""
        with closing(self._read_connection()) as connection:
            rows = connection.execute(
                "SELECT s.session_id, s.scenario_revision_id, s.status,"
                " (s.status IN ('READY', 'PAUSED', 'RUNNING') AND"
                " (EXISTS (SELECT 1 FROM session_runtimes r WHERE r.session_id=s.session_id)"
                " OR EXISTS (SELECT 1 FROM session_creations c WHERE c.session_id=s.session_id)))"
                " AS occupies_capacity FROM sessions s"
                " ORDER BY occupies_capacity DESC, s.rowid DESC",
            ).fetchall()
            return [{**dict(row), "occupies_capacity": bool(row["occupies_capacity"])} for row in rows]

    def delete_session(self, session_id: str) -> None:
        """Permanently remove a terminal attempt, including all of its history.

        Check the persisted lifecycle in the same writer transaction as deletion.
        Missing sessions are a successful retry after an uncertain HTTP response.
        """
        def write(connection: sqlite3.Connection) -> None:
            connection.execute("BEGIN IMMEDIATE")
            row = connection.execute("SELECT status FROM sessions WHERE session_id=?", (session_id,)).fetchone()
            if row is not None:
                if row["status"] not in {"ARCHIVED", "COMPLETED", "FAILED"}:
                    raise TrainingStoreError("INVALID_TRANSITION", "Archive the session before permanently deleting it")
                for table in ("session_creations", "session_runtimes", "commands", "events", "checkpoints", "sessions"):
                    connection.execute(f"DELETE FROM {table} WHERE session_id=?", (session_id,))
            connection.commit()
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
        with closing(self._read_connection()) as connection:
            rows = connection.execute(
                "SELECT s.session_id FROM sessions s WHERE s.status IN ('READY', 'PAUSED', 'RUNNING')"
                " AND (EXISTS (SELECT 1 FROM session_runtimes r WHERE r.session_id=s.session_id)"
                " OR EXISTS (SELECT 1 FROM session_creations c WHERE c.session_id=s.session_id))",
            ).fetchall()
            return [row[0] for row in rows]

    def runtime(self, session_id: str) -> tuple[FullSnapshot, dict]:
        with closing(self._read_connection()) as connection:
            row = connection.execute(
                "SELECT s.*, r.checkpoint_json, r.state_hash AS runtime_hash FROM sessions s "
                "LEFT JOIN session_runtimes r USING(session_id) WHERE s.session_id=?", (session_id,),
            ).fetchone()
            if row is None:
                raise TrainingStoreError("INVALID_REFERENCE", "Training session was not found")
            if row["checkpoint_json"] is None or _hash(row["checkpoint_json"]) != row["runtime_hash"]:
                raise TrainingStoreError("CORRUPT_CHECKPOINT", "Actor checkpoint hash does not match")
            return _load_snapshot(row), json.loads(row["checkpoint_json"])

    def runtimes(self) -> list[tuple[FullSnapshot, dict]]:
        return [self.runtime(session_id) for session_id in self.runtime_ids()]

    def create_session(self, snapshot: FullSnapshot, *, runtime_state: dict | None = None,
                       creation: tuple[str, str, str] | None = None) -> FullSnapshot:
        snapshot = FullSnapshot.model_validate(snapshot.model_dump(mode="json"))
        def write(connection: sqlite3.Connection) -> FullSnapshot:
            connection.execute("BEGIN IMMEDIATE")
            self._write_snapshot(connection, snapshot, initial=True)
            self._write_runtime(connection, snapshot.session_id, runtime_state)
            if creation is not None:
                connection.execute("INSERT INTO session_creations VALUES (?, ?, ?, ?)", (*creation, snapshot.session_id))
            connection.commit()
            return snapshot
        return self._submit(write)

    def _read_connection(self) -> sqlite3.Connection:
        connection = sqlite3.connect(f"file:{self.path.as_posix()}?mode=ro", uri=True)
        connection.row_factory = sqlite3.Row
        return connection

    def snapshot(self, session_id: str) -> FullSnapshot:
        with closing(self._read_connection()) as connection:
            row = connection.execute("SELECT * FROM sessions WHERE session_id=?", (session_id,)).fetchone()
            if row is None:
                raise TrainingStoreError("INVALID_REFERENCE", "Training session was not found")
            return _load_snapshot(row)

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
        with closing(self._read_connection()) as connection:
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
        }[command.command_type]
        if command.target_type != expected_target:
            raise TrainingStoreError("INVALID_PAYLOAD", "Command type and target type do not match")
        payload_hash = _hash(_json(command.model_dump(mode="json", exclude={"command_id", "idempotency_key"})))
        def write(connection: sqlite3.Connection) -> CommandStatus:
            connection.execute("BEGIN IMMEDIATE")
            session = connection.execute("SELECT * FROM sessions WHERE session_id=?", (command.session_id,)).fetchone()
            if session is None:
                raise TrainingStoreError("INVALID_REFERENCE", "Training session was not found")
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
               trace_fields: dict) -> CommandStatus:
        """Commit one actor-staged boundary, rechecking preconditions in the writer.

        On a stale command or staged boundary, the proposed state is discarded
        and a durable REJECTED outcome is written. A valid command requires a
        revised target snapshot. An actor may supply rejection_code when its
        own revalidation finds a changed constraint after acceptance.
        """
        if proposed is not None:
            proposed = FullSnapshot.model_validate(proposed.model_dump(mode="json"))
        domain_events = tuple(Event.model_validate(event.model_dump(mode="json")) for event in domain_events)
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
                # Another client may still be reconciling an accepted command.
                # Retiring the actor must settle that receipt in this transaction.
                pending = connection.execute(
                    "SELECT command_id, target_revision FROM commands"
                    " WHERE session_id=? AND status='QUEUED' AND command_id<>? ORDER BY rowid",
                    (result.session_id, command_id),
                ).fetchall()
                for waiting in pending:
                    result.event_sequence += 1
                    expired = self._event(result, result.event_sequence, "COMMAND_EXPIRED", "COMMAND",
                                          waiting["command_id"], waiting["target_revision"],
                                          {"status": "EXPIRED", "failure_code": "INVALID_TRANSITION"})
                    self._append_event(connection, result.session_id, expired)
                    connection.execute(
                        "UPDATE commands SET status='EXPIRED', finished_at_utc=?, result_event_id=?,"
                        " failure_code='INVALID_TRANSITION' WHERE command_id=?",
                        (_now(), expired.event_id, waiting["command_id"]),
                    )
            result.event_sequence += 1
            result.snapshot_id = str(uuid4())
            event = self._event(result, result.event_sequence, f"COMMAND_{status}", "COMMAND",
                                command_id, target_revision, {"status": status, "failure_code": code})
            self._append_event(connection, result.session_id, event)
            self._write_snapshot(connection, result)
            if status == "APPLIED":
                self._write_runtime(connection, result.session_id, runtime_state)
            connection.execute(
                "UPDATE commands SET status=?, finished_at_utc=?, applied_tick=?, result_event_id=?, failure_code=?, target_revision=? WHERE command_id=?",
                (status, _now(), result.tick if status == "APPLIED" else None,
                 event.event_id, code, target_revision, command_id),
            )
            row = connection.execute("SELECT * FROM commands WHERE command_id=?", (command_id,)).fetchone()
            connection.commit()
            return self._status(row, result)
        return self._submit(write)

    @staticmethod
    def _check_events(events: tuple[Event, ...], after: int, tick: int) -> None:
        if [event.event_sequence for event in events] != list(range(after + 1, after + 1 + len(events))):
            raise TrainingStoreError("STALE_REVISION", "Domain event sequence has a gap")
        if any(event.tick != tick for event in events):
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
        """Persist an actor publication and its ordered events before visibility."""
        snapshot = FullSnapshot.model_validate(snapshot.model_dump(mode="json"))
        domain_events = tuple(Event.model_validate(event.model_dump(mode="json")) for event in domain_events)
        def write(connection: sqlite3.Connection) -> FullSnapshot:
            connection.execute("BEGIN IMMEDIATE")
            row = connection.execute("SELECT * FROM sessions WHERE session_id=?", (snapshot.session_id,)).fetchone()
            if row is None:
                raise TrainingStoreError("INVALID_REFERENCE", "Training session was not found")
            previous = _load_snapshot(row)
            if ((expected_snapshot_id is not None and previous.snapshot_id != expected_snapshot_id) or
                snapshot.session_epoch != previous.session_epoch or
                snapshot.event_sequence != previous.event_sequence + len(domain_events) or
                snapshot.tick < previous.tick or
                (snapshot.tick == previous.tick and not domain_events) or
                snapshot.scene_revision != previous.scene_revision or
                snapshot.spatial != previous.spatial or
                snapshot.transform_revision != previous.transform_revision or
                snapshot.session.scenario_revision_id != previous.session.scenario_revision_id):
                raise TrainingStoreError("STALE_REVISION", "Publication does not follow the committed boundary")
            self._check_events(domain_events, previous.event_sequence, snapshot.tick)
            for event in domain_events:
                self._append_event(connection, snapshot.session_id, event)
            snapshot.snapshot_id = str(uuid4())
            self._write_snapshot(connection, snapshot)
            self._write_runtime(connection, snapshot.session_id, runtime_state)
            connection.commit()
            return snapshot
        return self._submit(write)

    def history(self, session_id: str, start_tick: int, end_tick: int) -> list[FullSnapshot]:
        """One committed checkpoint per tick, bounded to ten seconds at 20 Hz."""
        if start_tick < 0 or end_tick < start_tick or end_tick - start_tick > 200:
            raise TrainingStoreError("INVALID_PAYLOAD", "History window must be at most 200 ticks")
        with closing(self._read_connection()) as connection:
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
        with closing(self._read_connection()) as connection:
            rows = connection.execute(
                "SELECT event_json FROM events WHERE session_id=? AND event_sequence<=?"
                " AND json_extract(event_json, '$.entity_id')=? ORDER BY event_sequence",
                (session_id, through_sequence, entity_id),
            ).fetchall()
            return [Event.model_validate_json(row[0]) for row in rows]

    def incident_attempts(self, session_id: str, incident_id: str) -> list[dict]:
        with closing(self._read_connection()) as connection:
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
        with closing(self._read_connection()) as connection:
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
        with closing(self._read_connection()) as connection:
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
