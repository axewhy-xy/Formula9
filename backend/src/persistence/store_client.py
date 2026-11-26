"""The training store as a session worker process sees it.

A worker process (src/services/session_worker.py) runs live sessions away from the gateway process that
owns the SQLite writer. Its sessions call the same TrainingStore methods as they do in one process, on
this client: reads use the worker's own read-only SQLite connections, as TrainingStore's do (reading
takes no lock, and WAL mode lets them see every commit the gateway has made); writes go to the
gateway's single writer as one call each and wait for its answer, as they wait for the writer thread
in one process. Only plain data crosses: publications are encoded here (TrainingStore.encode_commit),
so the gateway's writer neither parses nor validates them; other writes carry JSON.

The client keeps its own copy of each of its sessions' latest committed snapshot JSON, as TrainingStore
does, from what its own writes committed. Only the worker that owns a session writes to it, so the copy
is never older than the row.
"""

from __future__ import annotations

from pathlib import Path
from threading import Lock, local
from typing import Any, Callable

from contracts.v2.models import CommandStatus, DispatcherCommand, Event, FullSnapshot
from src.services.assessment import AssessmentPlan
from src.services.assessment_policy import configured_policy
from src.core.training_telemetry import TrainingTrace
from src.persistence.training_store import _TERMINAL, PreparedAssessment, TrainingStore, TrainingStoreError, _EncodedRuntime


class _Open:
    """Stands for TrainingStore's lock file: the client holds none, but its reads look at .closed."""
    closed = False


class StoreClient(TrainingStore):
    """TrainingStore's interface over a call to the gateway's writer. call(kind, payload) sends one
    request and returns its answer, raising TrainingStoreError as the gateway's store raised it."""

    owns_writer = False

    def __init__(self, path: Path, call: Callable[[str, Any], Any], *, trace: TrainingTrace | None = None,
                 validate_publications: bool | None = None):
        # No TrainingStore.__init__: no lock file, writer thread or migrations; the gateway owns those
        import os
        if validate_publications is None:
            validate_publications = os.environ.get("F9_VALIDATE_PUBLICATIONS", "") not in {"", "0"}
        self.validate_publications = validate_publications
        self.assessment_policy = configured_policy()
        self._assessment_owners = {}
        self._published: dict[str, str] = {}
        self.trace = trace if trace is not None else TrainingTrace()
        self.path = Path(path).resolve()
        self._lock_file = _Open()
        self._readers = local()
        self._reader_connections = []
        self._readers_lock = Lock()
        self._call = call

    def close(self) -> None:
        self._lock_file.closed = True
        with self._readers_lock:
            readers, self._reader_connections = self._reader_connections, []
        for connection in readers:
            connection.close()

    def _submit_async(self, operation):
        raise TrainingStoreError("SESSION_OWNED", "A session worker writes through the gateway's store")

    def _published_as(self, session_id: str, raw: str | None) -> None:
        if raw is None:
            self._published.pop(session_id, None)
        else:
            self._published[session_id] = raw

    def next_session_number(self) -> int:
        return self._call("next_session_number", None)

    def create_session(self, snapshot: FullSnapshot, *, runtime_state: dict | None = None,
                       creation: tuple[str, str, str] | None = None,
                       assessment_plan: AssessmentPlan | None = None, name: str | None = None) -> FullSnapshot:
        reply = self._call("create_session", {"snapshot": snapshot.model_dump_json(),
                                              "runtime": _EncodedRuntime.of(runtime_state), "creation": creation,
                                              "assessment_plan": assessment_plan.model_dump_json() if assessment_plan else None,
                                              "name": name})
        self._published_as(snapshot.session_id, reply["published"])
        return FullSnapshot.model_validate_json(reply["snapshot"])

    def _accept(self, actor_id: str, command: DispatcherCommand, *, defer_preconditions: bool = False) -> CommandStatus:
        reply = self._call("accept", {"actor_id": actor_id, "command": command.model_dump_json(),
                                      "defer_preconditions": defer_preconditions})
        return CommandStatus.model_validate_json(reply)

    def _apply(self, command_id: str, proposed: FullSnapshot | None = None,
               domain_events: tuple[Event, ...] = (), *, rejection_code: str | None = None,
               expected_snapshot_id: str | None = None, runtime_state: dict | None = None,
               trace_fields: dict, prepared_assessment: PreparedAssessment | None = None) -> CommandStatus:
        # Evaluate terminal decisions in the owning worker, before asking the shared writer to commit.
        prepared = prepared_assessment
        if prepared is None and proposed is not None:
            prepared = self.prepare_assessment(proposed, domain_events)
        reply = self._call("apply", {
            "command_id": command_id, "proposed": proposed.model_dump_json() if proposed is not None else None,
            "events": [event.model_dump_json() for event in domain_events], "rejection_code": rejection_code,
            "expected_snapshot_id": expected_snapshot_id, "runtime": _EncodedRuntime.of(runtime_state),
            "assessment": prepared})
        trace_fields.update(reply["trace"])
        result = CommandStatus.model_validate_json(reply["status"])
        if "published" in reply:  # Absent when the command was already settled: nothing was published
            self._published_as(result.session_id, reply["published"])
        return result

    def _commit_snapshot(self, snapshot: FullSnapshot, domain_events: tuple[Event, ...] = (), *,
                         expected_snapshot_id: str | None = None, runtime_state: dict | None = None) -> FullSnapshot:
        commit = self.encode_commit(snapshot, domain_events, expected_snapshot_id=expected_snapshot_id,
                                    runtime_state=runtime_state)
        self._call("commit", commit)
        self._published_as(snapshot.session_id,
                           None if commit.snapshot.status in _TERMINAL else commit.snapshot.raw)
        if commit.snapshot.status in _TERMINAL:
            return self.snapshot(snapshot.session_id)
        snapshot.snapshot_id = commit.snapshot_id
        return snapshot

    def delete_session(self, session_id: str, *, actor_id: str | None = None) -> None:
        raise TrainingStoreError("SESSION_OWNED", "Only the gateway deletes sessions")

    def compact(self) -> dict:
        raise TrainingStoreError("SESSION_OWNED", "Only the gateway compacts the store")

    def recover_session(self, session_id: str) -> None:
        raise TrainingStoreError("SESSION_OWNED", "Only the gateway recovers sessions")


def serve_store_call(store: TrainingStore, kind: str, payload: Any) -> Any:
    """The gateway's side of a StoreClient write other than a publication (those take
    TrainingStore.submit_commit without a thread): runs it on store and returns plain data."""
    if kind == "next_session_number":
        return store.next_session_number()
    if kind == "create_session":
        created = store.create_session(FullSnapshot.model_validate_json(payload["snapshot"]),
                                       runtime_state=payload["runtime"],
                                       creation=tuple(payload["creation"]) if payload["creation"] else None,
                                       assessment_plan=AssessmentPlan.model_validate_json(payload["assessment_plan"])
                                       if payload.get("assessment_plan") else None,
                                       name=payload.get("name"))
        return {"snapshot": created.model_dump_json(), "published": store.published_snapshot_json(created.session_id)}
    if kind == "accept":
        # The worker's TrainingStore.accept already records the command.accept span around this call
        return store._accept(payload["actor_id"], DispatcherCommand.model_validate_json(payload["command"]),
                             defer_preconditions=payload["defer_preconditions"]).model_dump_json()
    if kind == "apply":
        fields: dict = {}
        # What the writer published for this apply, taken there: read here after it returned, a later
        # commit of the session could already have replaced it, and the worker's copy would go back
        published: dict = {}
        result = store._apply(payload["command_id"],
                              FullSnapshot.model_validate_json(payload["proposed"]) if payload["proposed"] else None,
                              tuple(Event.model_validate_json(event) for event in payload["events"]),
                              rejection_code=payload["rejection_code"],
                              expected_snapshot_id=payload["expected_snapshot_id"],
                              runtime_state=payload["runtime"], trace_fields=fields, published=published,
                              prepared_assessment=payload.get("assessment"))
        reply = {"status": result.model_dump_json(), "trace": fields}
        if "raw" in published:
            reply["published"] = published["raw"]
        return reply
    raise ValueError(f"Unknown store call {kind!r}")
