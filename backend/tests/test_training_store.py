"""Durability and reconciliation cases for BE-M2-03."""

import json
import sqlite3
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from contracts.v2.models import DispatcherCommand, Event, FullSnapshot
from src.api.routes.training_v2 import get_actor_id, get_training_store
from src.app import app
from src.persistence.training_store import _MIGRATIONS, TrainingStore, TrainingStoreError

FIXTURES = Path(__file__).resolve().parents[2] / "contracts" / "v2" / "fixtures"


def fixture(name: str) -> dict:
    return json.loads((FIXTURES / f"{name}.json").read_text(encoding="utf-8"))


def initial() -> FullSnapshot:
    return FullSnapshot.model_validate(fixture("snapshot"))


def command(**changes: object) -> DispatcherCommand:
    value = fixture("command")
    value.update(changes)
    return DispatcherCommand.model_validate(value)


def revised(snapshot: FullSnapshot) -> FullSnapshot:
    result = snapshot.model_copy(deep=True)
    result.incidents[0].aggregate_revision += 1
    result.incidents[0].state = "ACTION_COMMITTED"
    result.tick += 1
    result.simulation_time_s += 0.05
    return result


@pytest.mark.parametrize("status", ["ARCHIVED", "COMPLETED", "FAILED"])
def test_delete_terminal_session_is_durable_even_with_corrupt_snapshot(tmp_path, status):
    path = tmp_path / "delete.sqlite"
    with TrainingStore(path) as store:
        snapshot = initial()
        snapshot.session.status = status
        store.create_session(snapshot, runtime_state={"version": 1}, creation=("operator", "key", "hash"))
        # Metadata-based cleanup must also work when terminal history cannot load.
        with sqlite3.connect(path) as connection:
            connection.execute("UPDATE sessions SET snapshot_json='corrupt'")
        store.delete_session(snapshot.session_id)
        store.delete_session(snapshot.session_id)
    with TrainingStore(path) as store:
        assert store.list_sessions() == [] and store.runtime_ids() == []
        assert store.creation("operator", "key", "hash") is None
        with pytest.raises(TrainingStoreError, match="not found"):
            store.snapshot(snapshot.session_id)


def test_delete_failure_rolls_back_every_related_record(tmp_path):
    path = tmp_path / "atomic-delete.sqlite"
    with TrainingStore(path) as store:
        snapshot = initial()
        snapshot.session.status = "ARCHIVED"
        store.create_session(snapshot, runtime_state={"version": 1}, creation=("operator", "key", "hash"))
        with sqlite3.connect(path) as connection:
            connection.execute("CREATE TRIGGER fail_delete BEFORE DELETE ON checkpoints BEGIN SELECT RAISE(ABORT, 'injected'); END")
        with pytest.raises(sqlite3.IntegrityError, match="injected"):
            store.delete_session(snapshot.session_id)
        assert store.creation("operator", "key", "hash").session_id == snapshot.session_id
        assert store.runtime(snapshot.session_id)[1] == {"version": 1}
        assert store.history(snapshot.session_id, 0, snapshot.tick)


def test_compact_rejects_nonterminal_sessions_without_a_live_actor(tmp_path):
    with TrainingStore(tmp_path / "compact.sqlite") as store:
        store.create_session(initial())
        with pytest.raises(TrainingStoreError, match="Archive all"):
            store.compact()


def test_compact_preserves_retained_terminal_history_and_runtime(tmp_path):
    path = tmp_path / "compact-retained.sqlite"
    snapshot = initial()
    snapshot.session.status = "ARCHIVED"
    with TrainingStore(path) as store:
        store.create_session(snapshot, runtime_state={"version": 1}, creation=("operator", "retained", "hash"))
        store.compact()
        assert store.snapshot(snapshot.session_id) == snapshot
        assert store.runtime(snapshot.session_id)[1] == {"version": 1}
        assert store.creation("operator", "retained", "hash") == snapshot
    with TrainingStore(path) as store:
        assert store.snapshot(snapshot.session_id) == snapshot
        assert store.history(snapshot.session_id, snapshot.tick, snapshot.tick) == [snapshot]


def test_acknowledgement_retry_and_payload_conflict(tmp_path: Path) -> None:
    with TrainingStore(tmp_path / "training.sqlite") as store:
        store.create_session(initial())
        first = store.accept("actor-a", command())
        assert first.status == "QUEUED" and first.applied_tick is None
        assert store.accept("actor-a", command(command_id="retry-id")).command_id == first.command_id
        with pytest.raises(TrainingStoreError, match="different command content") as error:
            store.accept("actor-a", command(reason="Changed reason"))
        assert error.value.code == "IDEMPOTENCY_CONFLICT"
        assert store.command_status("session-001", "actor-a", "client-key-1").status == "QUEUED"
        with pytest.raises(TrainingStoreError):
            store.command_status("session-001", "actor-b", "client-key-1")
        with sqlite3.connect(store.path) as connection:
            assert connection.execute("PRAGMA journal_mode").fetchone()[0] == "wal"
            assert connection.execute("SELECT version FROM schema_migrations").fetchone()[0] == 1
            assert connection.execute("SELECT reason FROM commands").fetchone()[0] == "Wait for the scripted obstruction to clear"


def test_database_migrated_by_a_newer_backend_is_refused(tmp_path: Path) -> None:
    path = tmp_path / "training.sqlite"
    with TrainingStore(path):
        pass
    latest = max(int(migration.name[:4]) for migration in _MIGRATIONS.glob("[0-9][0-9][0-9][0-9]_*.sql"))
    with sqlite3.connect(path) as connection:
        connection.execute("INSERT INTO schema_migrations VALUES (?, '2026-01-01T00:00:00+00:00')", (latest + 1,))
    with pytest.raises(TrainingStoreError) as error:
        TrainingStore(path)
    assert error.value.code == "SCHEMA_TOO_NEW"
    assert f"schema v{latest + 1}" in str(error.value) and f"up to v{latest}" in str(error.value)
    assert "TRAINING_DATABASE_PATH" in str(error.value)


def test_application_commits_status_event_and_checkpoint_once(tmp_path: Path) -> None:
    path = tmp_path / "training.sqlite"
    with TrainingStore(path) as store:
        store.create_session(initial())
        store.accept("actor-a", command())
        applied = store.apply("command-1", revised(store.snapshot("session-001")))
        assert applied.status == "APPLIED" and applied.applied_tick == 201
        assert applied.result_event_id
        assert store.apply("command-1").result_event_id == applied.result_event_id
        assert store.snapshot("session-001").incidents[0].aggregate_revision == 3
        events = store.replay("session-001", 7)
        assert [event.event_type for event in events.events] == ["COMMAND_APPLIED"]
        assert events.next_sequence == 8
    with TrainingStore(path) as recovered:
        status = recovered.command_status("session-001", "actor-a", "client-key-1")
        assert status.status == "APPLIED" and status.result_event_id == applied.result_event_id
        assert recovered.snapshot("session-001").session.status == "PAUSED"
        assert recovered.snapshot("session-001").session_epoch == 2
        assert [event.event_type for event in recovered.replay("session-001", 8).events] == ["SESSION_RECOVERED"]


def test_accepted_command_requires_reconfirmation_after_restart(tmp_path: Path) -> None:
    path = tmp_path / "training.sqlite"
    with TrainingStore(path) as store:
        store.create_session(initial())
        store.accept("actor-a", command())
    with TrainingStore(path) as recovered:
        status = recovered.accept("actor-a", command())
        assert status.status == "NEEDS_RECONFIRMATION"
        assert recovered.apply("command-1").status == "NEEDS_RECONFIRMATION"
        assert recovered.snapshot("session-001").session_epoch == 2
        assert [event.event_type for event in recovered.replay("session-001", 7).events] == [
            "COMMAND_NEEDS_RECONFIRMATION", "SESSION_RECOVERED"]


def test_stale_at_application_is_durable_rejection(tmp_path: Path) -> None:
    with TrainingStore(tmp_path / "training.sqlite") as store:
        store.create_session(initial())
        store.accept("actor-a", command())
        store.commit_snapshot(revised(store.snapshot("session-001")))
        rejected = store.apply("command-1")
        assert rejected.status == "REJECTED" and rejected.failure_code == "STALE_REVISION"
        assert rejected.applied_tick is None
        assert store.apply("command-1").result_event_id == rejected.result_event_id
        assert [event.event_type for event in store.replay("session-001", 7).events] == ["COMMAND_REJECTED"]


def test_failed_application_transaction_leaves_command_queued(tmp_path: Path, monkeypatch) -> None:
    with TrainingStore(tmp_path / "training.sqlite") as store:
        store.create_session(initial())
        store.accept("actor-a", command())
        original = TrainingStore._write_snapshot

        def crash(*_: object, **__: object) -> None:
            raise RuntimeError("injected write failure")

        monkeypatch.setattr(TrainingStore, "_write_snapshot", staticmethod(crash))
        with pytest.raises(RuntimeError, match="injected"):
            store.apply("command-1", revised(store.snapshot("session-001")))
        assert store.command_status("session-001", "actor-a", "client-key-1").status == "QUEUED"
        assert store.replay("session-001", 7).events == []
        monkeypatch.setattr(TrainingStore, "_write_snapshot", staticmethod(original))
        assert store.apply("command-1", revised(store.snapshot("session-001"))).status == "APPLIED"


def test_domain_events_and_snapshot_commit_together(tmp_path: Path) -> None:
    with TrainingStore(tmp_path / "training.sqlite") as store:
        before = store.create_session(initial())
        next_snapshot = revised(before)
        next_snapshot.event_sequence = 8
        event = Event(event_id="incident-update-8", event_sequence=8, tick=201,
                      entity_type="INCIDENT", entity_id="incident-1", entity_revision=3,
                      event_type="ACTION_COMMITTED", source=before.source, data={})
        store.commit_snapshot(next_snapshot, (event,))
        assert store.snapshot("session-001").event_sequence == 8
        assert [item.event_id for item in store.replay("session-001", 7).events] == ["incident-update-8"]


def test_database_has_one_process_owner(tmp_path: Path) -> None:
    path = tmp_path / "training.sqlite"
    with TrainingStore(path):
        with pytest.raises(TrainingStoreError) as error:
            TrainingStore(path)
        assert error.value.code == "SESSION_OWNED"


def test_rest_status_and_replay_are_scoped_to_authenticated_actor(tmp_path: Path) -> None:
    with TrainingStore(tmp_path / "training.sqlite") as store:
        store.create_session(initial())
        app.dependency_overrides[get_training_store] = lambda: store
        app.dependency_overrides[get_actor_id] = lambda: "actor-a"
        try:
            with TestClient(app) as client:
                accepted = client.post("/api/v2/sessions/session-001/commands", json=fixture("command"))
                assert accepted.status_code == 202 and accepted.json()["status"] == "QUEUED"
                status = client.get("/api/v2/sessions/session-001/commands/client-key-1")
                assert status.status_code == 200 and status.json()["command_id"] == "command-1"
                assert client.get("/api/v2/sessions/session-001/events?after=7").json()["events"] == []
                app.dependency_overrides[get_actor_id] = lambda: "actor-b"
                assert client.get("/api/v2/sessions/session-001/commands/client-key-1").status_code == 404
                del app.dependency_overrides[get_actor_id]
                assert client.get("/api/v2/sessions/session-001/snapshot").status_code == 403
        finally:
            app.dependency_overrides.clear()
