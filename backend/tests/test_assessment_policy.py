"""Regression checks for assessment privacy, retention, expiry and publication costs.

Author: Nur Syazliyarni Binte Zazali
Date Created: 23/11/2026
Last Modified: 23/11/2026
"""

import asyncio
from concurrent.futures import ThreadPoolExecutor
from datetime import UTC, datetime, timedelta
from threading import Event, current_thread

from fastapi.testclient import TestClient
import pytest

from backend.tests.test_assessment import approve_and_start, create_attempt, session_control
from backend.tests.test_session_workers import gateway, manual_clock
from contracts.v2.route_edit import RouteApprovalRequest, RouteEditRequest
from contracts.v2.sessions import CreateSessionRequest
from src.api.routes.training_v2 import get_actor_id, get_training_store
from src.app import app
from src.persistence import training_store
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.services.assessment import SCORED_SCENARIO, Rubric, load_rubric
from src.services.assessment_policy import AssessmentPolicy
from src.services.scored_actor import ScoredSessionActor
from src.services.training_sessions import TrainingSessionService, checkpoint
from src.services.training_workflow import approve_workflow_route


def test_private_reads_and_retention_use_the_authenticated_actor(tmp_path):
    policy = AssessmentPolicy(supervisors=frozenset({"supervisor"}))
    with TrainingStore(tmp_path / "private.sqlite", assessment_policy=policy) as store:
        snapshot, _ = create_attempt(store)
        sid = snapshot.session_id
        request = CreateSessionRequest(scenario_revision_id="scenario-m2-r1")
        practice, _ = TrainingSessionService(store)._create_initial(
            request, "another-trainee", "shared-practice", TrainingSessionService._creation_digest(request))
        actor = ["trainee"]
        previous = dict(app.dependency_overrides)
        app.dependency_overrides[get_training_store] = lambda: store
        app.dependency_overrides[get_actor_id] = lambda: actor[0]
        try:
            with TestClient(app) as client:
                assert client.get(f"/api/v2/sessions/{sid}/snapshot").status_code == 200
                actor[0] = "observer"
                for suffix in ("snapshot", "events", "assessment", "traffic", "signals/state",
                               "routes/route-peer-a/editor", "incidents/assessment-road-block/history"):
                    assert client.get(f"/api/v2/sessions/{sid}/{suffix}").status_code == 403, suffix
                assert client.get(f"/api/v2/sessions/{practice.session_id}/snapshot").status_code == 200
                assert client.get(f"/api/v2/sessions/{practice.session_id}/events").status_code == 200
                assert [row["session_id"] for row in client.get("/api/v2/sessions").json()["sessions"]] == [practice.session_id]
                with client.websocket_connect(f"/api/v2/sessions/{sid}/stream") as websocket:
                    assert websocket.receive_json()["error"]["code"] == "ROLE_DENIED"

                actor[0] = "supervisor"
                assert client.get(f"/api/v2/sessions/{sid}/snapshot").status_code == 200
                listed = client.get("/api/v2/sessions").json()
                assert listed["sessions"][0]["can_archive"] is True
                assert listed["terminal_practice_count"] == 0
                assert client.post(f"/api/v2/sessions/{sid}/commands",
                                   json=session_control(store.snapshot(sid), "ARCHIVE").model_dump()).status_code == 202
                actor[0] = "trainee"
                assert client.get(f"/api/v2/sessions/{sid}/assessment").json()["status"] == "INCOMPLETE"
                assert client.delete(f"/api/v2/sessions/{sid}").status_code == 403
                entries = client.get("/api/v2/sessions").json()["sessions"]
                assert next(row for row in entries if row["session_id"] == sid)["can_delete"] is False
                actor[0] = "supervisor"
                assert client.delete(f"/api/v2/sessions/{sid}").status_code == 204
        finally:
            app.dependency_overrides.clear()
            app.dependency_overrides.update(previous)


def test_archive_after_auto_completion_reports_that_the_attempt_already_ended(tmp_path):
    with TrainingStore(tmp_path / "completed-archive.sqlite") as store:
        initial, actor = create_attempt(store)
        actor = approve_and_start(store, initial, actor)
        actor.current_tick = actor.assessment_plan.ticks(actor.assessment_plan.rubric.exercise_limit_s) - 1
        _, terminal, _ = TrainingSessionService(store)._step_staged(actor, None)
        saved = store.assessment(initial.session_id, "trainee")
        previous = dict(app.dependency_overrides)
        app.dependency_overrides[get_training_store] = lambda: store
        app.dependency_overrides[get_actor_id] = lambda: "trainee"
        try:
            with TestClient(app) as client:
                response = client.post(f"/api/v2/sessions/{initial.session_id}/commands",
                    json=session_control(terminal, "ARCHIVE").model_dump())
                assert response.status_code == 400
                assert response.json()["error"]["code"] == "INVALID_TRANSITION"
                assert "already ended" in response.json()["error"]["message"]
                assert store.assessment(initial.session_id, "trainee") == saved
        finally:
            app.dependency_overrides.clear()
            app.dependency_overrides.update(previous)


def test_practice_purge_excludes_retained_assessments_from_its_count(tmp_path):
    with TrainingStore(tmp_path / "mixed-purge.sqlite") as store:
        service = TrainingSessionService(store)
        assessment_ids = []
        for index in range(30):
            assessed = index >= 20
            request = CreateSessionRequest(scenario_revision_id=SCORED_SCENARIO if assessed else "scenario-m2-r1")
            snapshot, actor = service._create_initial(
                request, "trainee", f"attempt-{index}", service._creation_digest(request))
            archive = session_control(snapshot, "ARCHIVE").model_copy(
                update={"command_id": f"archive-{index}", "idempotency_key": f"archive-{index}"})
            receipt, _ = service._submit_staged(actor, "trainee", archive, False)
            assert receipt.status == "APPLIED"
            if assessed:
                assessment_ids.append(snapshot.session_id)
        page = store.session_page(1, actor_id="trainee")
        assert page["total"] == 30 and page["terminal_practice_count"] == 20
        assert store.expired_session_ids(20) == []
        assert all(store.assessment(sid, "trainee").status == "INCOMPLETE" for sid in assessment_ids)


@pytest.mark.parametrize("paused", [False, True])
def test_inactive_attempts_expire_after_restart_and_keep_their_report(tmp_path, monkeypatch, paused):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    path = tmp_path / "expiry.sqlite"
    with TrainingStore(path) as store:
        snapshot, actor = create_attempt(store)
        if paused:
            actor = approve_and_start(store, snapshot, actor)
            TrainingSessionService(store)._submit_staged(
                actor, "trainee", session_control(store.snapshot(snapshot.session_id), "PAUSE"), False)
        with store._reading() as connection:
            activity = connection.execute("SELECT activity_at_utc FROM session_assessments").fetchone()[0]
        sid = snapshot.session_id

    async def run():
        with TrainingStore(path) as store:
            service = TrainingSessionService(store)
            try:
                await service.initialize()
                before = datetime.fromisoformat(activity) + timedelta(seconds=899)
                assert await service.expire_inactive_assessments(now=before) == 0
                assert service.owns(sid)
                assert await service.expire_inactive_assessments(now=before + timedelta(seconds=2)) == 1
                assert not service.owns(sid)
                assert store.snapshot(sid).session.status == "ARCHIVED"
                assert store.assessment(sid, "trainee").status == "INCOMPLETE"
                assert store.expired_session_ids(0) == []
            finally:
                await service.close()
    asyncio.run(run())


def test_worker_expiry_releases_capacity_and_retains_the_result(tmp_path):
    async def run():
        async with gateway(tmp_path / "worker-expiry.sqlite", manual_clock=True) as (store, service):
            snapshot = await service.create(CreateSessionRequest(scenario_revision_id=SCORED_SCENARIO),
                                            "trainee", "abandoned")
            assert await service.expire_inactive_assessments(
                now=datetime.now(UTC) + timedelta(seconds=901)) == 1
            assert not service.owns(snapshot.session_id)
            assert store.assessment(snapshot.session_id, "trainee").status == "INCOMPLETE"
    asyncio.run(run())


def test_an_owner_action_wins_over_an_earlier_expiry_check(tmp_path, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)

    async def run():
        with TrainingStore(tmp_path / "activity-race.sqlite") as store:
            snapshot, actor = create_attempt(store)
            service = TrainingSessionService(store)
            try:
                await service.initialize()
                with store._reading() as connection:
                    cutoff = connection.execute("SELECT activity_at_utc FROM session_assessments").fetchone()[0]
                routes = actor.definition.routes.for_route(snapshot, "route-peer-a")
                preview = routes.draft_preview(snapshot, RouteEditRequest(
                    route_id="route-peer-a", expected_route_revision=1, draft_revision=1))
                request = RouteApprovalRequest(preview_id=preview.preview_id, preview_revision=preview.preview_revision,
                    command_id="route", idempotency_key="route", target_vehicle_ids=["shuttle-01"], reason="Owner is active")
                assert (await service.approve_route("trainee", snapshot.session_id, request)).status == "APPLIED"
                archive = session_control(store.snapshot(snapshot.session_id), "ARCHIVE")
                assert not await service._expire_assessment("trainee", archive, cutoff)
                assert store.snapshot(snapshot.session_id).session.status == "READY"
            finally:
                await service.close()
    asyncio.run(run())


def test_grading_does_not_block_the_database_writer(tmp_path, monkeypatch):
    entered, release = Event(), Event()
    original = training_store.finalize

    def slow_grade(*args):
        assert current_thread().name != "training-sqlite-writer"
        entered.set()
        assert release.wait(5), "Test did not release its blocked evaluator"
        return original(*args)

    monkeypatch.setattr(training_store, "finalize", slow_grade)
    with TrainingStore(tmp_path / "writer.sqlite") as store, ThreadPoolExecutor(max_workers=2) as pool:
        snapshot, actor = create_attempt(store)
        task = pool.submit(TrainingSessionService(store)._submit_staged, actor, "trainee",
                           session_control(snapshot, "ARCHIVE"), False)
        try:
            assert entered.wait(3)
            assert pool.submit(store.next_session_number).result(timeout=2) > 0
        finally:
            release.set()
        assert task.result(timeout=3)[0].status == "APPLIED"
        report = store.assessment(snapshot.session_id, "trainee")
        assert report.through_event_sequence == store.snapshot(snapshot.session_id).event_sequence


def test_prepared_grade_is_discarded_if_the_attempt_changes(tmp_path):
    with TrainingStore(tmp_path / "stale-grade.sqlite") as store:
        snapshot, actor = create_attempt(store)
        terminal = snapshot.model_copy(deep=True)
        terminal.session.status = "ARCHIVED"
        terminal.session.aggregate_revision += 1
        event = TrainingSessionService._event(terminal, "SESSION_ARCHIVED")
        terminal.event_sequence += 1
        commit = store.encode_commit(terminal, (event,), expected_snapshot_id=snapshot.snapshot_id)
        assert commit.assessment is not None
        approve_and_start(store, snapshot, actor)
        with pytest.raises(TrainingStoreError, match="committed boundary"):
            store.submit_commit(commit).result()
        assert store.assessment(snapshot.session_id, "trainee").status == "IN_PROGRESS"


def test_new_identity_ignores_metadata_and_keeps_behavioral_changes_distinct():
    rubric = load_rubric()
    changed = rubric.model_dump()
    changed["metadata"].update(author="Other author", last_modified="24/11/2026")
    original = ScoredSessionActor(rubric, session_id="original")
    cosmetic = ScoredSessionActor(Rubric.model_validate(changed), session_id="cosmetic")
    assert original.scenario_revision == cosmetic.scenario_revision
    changed["rules"]["route_time"]["full_ratio"] = 1.05
    revised = ScoredSessionActor(Rubric.model_validate(changed), session_id="revised")
    assert revised.scenario_revision != original.scenario_revision


def test_charging_windows_validate_the_offer_delay_and_observe_late_offers():
    rubric = load_rubric()
    invalid = rubric.model_dump()
    invalid["charging_offer_deadline_s"] = 31
    with pytest.raises(ValueError, match="must fit inside"):
        Rubric.model_validate(invalid)
    actor = ScoredSessionActor(rubric, session_id="delayed-offer")
    actor.current_tick = actor.assessment_plan.ticks(15)
    snapshot = actor.generate_snapshot()
    snapshot.session.status = "RUNNING"
    actor.observe_publication(snapshot, ())
    assert actor.observations.charging_offer_tick == actor.current_tick
    actor.current_tick += actor.assessment_plan.ticks(rubric.charging_observation_s)
    actor._observe_charging()
    assert actor.observations.charging_finished
    assert actor.current_tick < actor.assessment_plan.ticks(rubric.exercise_limit_s)


def test_unavailable_charging_task_cannot_start_a_new_attempt():
    actor = ScoredSessionActor(load_rubric(), session_id="no-charging-task")
    snapshot = actor.generate_snapshot()
    snapshot.session.status = "RUNNING"
    snapshot.charge_options = []
    with pytest.raises(TrainingStoreError, match="charging task is unavailable"):
        actor.observe_publication(snapshot, ())


def test_start_rejection_reports_the_charging_problem_and_keeps_the_ready_attempt(tmp_path, monkeypatch):
    with TrainingStore(tmp_path / "unavailable.sqlite") as store:
        snapshot, actor = create_attempt(store)
        preview = actor.definition.routes.for_route(snapshot, "route-peer-a").draft_preview(snapshot, RouteEditRequest(
            route_id="route-peer-a", expected_route_revision=1, draft_revision=1))
        approval = RouteApprovalRequest(preview_id=preview.preview_id, preview_revision=preview.preview_revision,
            command_id="approve", idempotency_key="approve", target_vehicle_ids=["shuttle-01"], reason="Pre-trip detour")
        _, actor = approve_workflow_route(store, actor, "trainee", approval, checkpoint)
        observe = ScoredSessionActor.observe_publication

        def without_options(self, published, events):
            published.charge_options = []
            return observe(self, published, events)

        monkeypatch.setattr(ScoredSessionActor, "observe_publication", without_options)
        current = store.snapshot(snapshot.session_id)
        receipt, staged = TrainingSessionService(store)._submit_staged(
            actor, "trainee", session_control(current, "START"), False)
        assert receipt.status == "REJECTED" and receipt.failure_code == "ASSESSMENT_NOT_READY"
        assert staged is None and store.snapshot(snapshot.session_id).session.status == "READY"
