"""Regression coverage for Formula9's scored exercise.

Author: Nur Syazliyarni Binte Zazali
Date Created: 15/11/2026
Last Modified: 16/11/2026

Tests the scoring rules against explicit evidence and exercises the real
command, session completion and SQLite recovery paths without a live network.
"""

from copy import deepcopy
from dataclasses import replace
from hashlib import sha256
import json
from uuid import uuid4

import pytest

from contracts.v2.models import DispatcherCommand, Event
from contracts.v2.route_edit import RouteApprovalRequest, RouteEditRequest
from contracts.v2.sessions import CreateSessionRequest
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.services.assessment import (EVALUATOR_VERSIONS, SCORED_INCIDENT, SCORED_SCENARIO, Criterion, Rubric,
                                     finalize, load_rubric)
from src.services.integrated_actor import SHUTTLE_FRONT_M, IntegratedSessionActor
from src.services.scored_actor import ScoredSessionActor
from src.services.training_sessions import TrainingSessionService, checkpoint
from src.services.training_workflow import approve_workflow_route, publish_workflow


def session_control(snapshot, action):
    """Use an ordinary revision-checked, idempotent session command."""
    return DispatcherCommand(contract_version="2.0", command_id=action.lower(), idempotency_key=action.lower(),
        session_id=snapshot.session_id, command_type=action, expected_epoch=snapshot.session_epoch,
        expected_revision=snapshot.session.aggregate_revision, target_type="SESSION",
        target_id=snapshot.session_id, payload={})


def create_attempt(store):
    request = CreateSessionRequest(scenario_revision_id=SCORED_SCENARIO)
    return TrainingSessionService(store)._create_initial(request, "trainee", "create-scored",
        sha256(request.model_dump_json().encode()).hexdigest())


def approve_and_start(store, snapshot, actor):
    """Approve an actual route around the authored closure, then start through the live service."""
    service = TrainingSessionService(store)
    route = snapshot.routes[0]
    routes = actor.definition.routes.for_route(snapshot, route.route_id)
    preview = routes.draft_preview(snapshot, RouteEditRequest(route_id=route.route_id,
        expected_route_revision=route.revision, draft_revision=1))
    assert preview.approvable
    approval = RouteApprovalRequest(command_id="approve", idempotency_key="approve",
        preview_id=preview.preview_id,
        preview_revision=preview.preview_revision, target_vehicle_ids=["shuttle-01"], reason="Serve all stops")
    result, actor = approve_workflow_route(store, actor, "trainee", approval, checkpoint)
    assert result.status == "APPLIED"
    result, actor = service._submit_staged(actor, "trainee", session_control(store.snapshot(snapshot.session_id), "START"), False)
    assert result.status == "APPLIED"
    return actor


def test_scored_attempt_recovers_and_finalizes_once(tmp_path):
    path = tmp_path / "training.sqlite"
    with TrainingStore(path) as store:
        snapshot, actor = create_attempt(store)
        owner = "trainee"
        sid = snapshot.session_id
        assert store.assessment(sid, owner).status == "IN_PROGRESS"
        assert snapshot.disruptions and snapshot.scenario.seed == 1000
        assert snapshot.scenario.assessment_duration_s == 120
        for action in (lambda: store.assessment(sid, "observer"),
                       lambda: store.accept("observer", session_control(snapshot, "ARCHIVE"))):
            with pytest.raises(TrainingStoreError, match="assigned trainee"):
                action()
        actor = approve_and_start(store, snapshot, actor)
        assert actor.observations.charging_offer_tick is not None
    with TrainingStore(path) as store:
        snapshot, state = store.runtime(sid)
        service = TrainingSessionService(store)
        actor = service._restore_session(snapshot, state).actor
        assert actor.observations.charging_offer_tick is not None
        assert actor.assessment_plan.rubric.revision == "toa-payoh-rubric-r2"
        _, actor = service._submit_staged(actor, owner, session_control(snapshot, "RESUME"), False)
        paused = session_control(store.snapshot(sid), "PAUSE")
        assert store.accept(owner, paused).status == "QUEUED"
        # Exercise the real terminal publication without simulating fifteen minutes in this unit test.
        actor.current_tick = actor.assessment_plan.ticks(actor.assessment_plan.rubric.exercise_limit_s) - 1
        _, terminal, _ = service._step_staged(actor, None)
        saved = store.assessment(sid, owner)
        assert terminal.session.status == "COMPLETED"
        assert saved.status == "INCOMPLETE"
        assert saved.through_event_sequence == terminal.event_sequence
        assert [e.event_type for e in store.replay(sid, terminal.event_sequence - 2).events] == [
            "SESSION_COMPLETED", "COMMAND_EXPIRED"]
        assert store.accept(owner, paused).status == "EXPIRED"
        assert sid not in store.runtime_ids() and sid not in store.expired_session_ids(0)
    with TrainingStore(path) as store:
        assert store.assessment(sid, "trainee") == saved


def test_archiving_a_scored_attempt_preserves_incomplete_report(tmp_path):
    with TrainingStore(tmp_path / "training.sqlite") as store:
        snapshot, actor = create_attempt(store)
        control = session_control(snapshot, "ARCHIVE")
        result, _ = TrainingSessionService(store)._submit_staged(actor, "trainee", control, False)
        report = store.assessment(snapshot.session_id, "trainee")
        assert report.status == "INCOMPLETE"
        assert store.accept("trainee", control) == result
        with pytest.raises(TrainingStoreError, match="assigned trainee"):
            store.delete_session(snapshot.session_id, actor_id="observer")
        store.delete_session(snapshot.session_id, actor_id="trainee")


def legacy_rubric():
    """Reproduce the pinned rubric in checkpoints made before the two-minute exercise."""
    data = load_rubric().model_dump(exclude={"short_exercise", "evaluator_version"})
    data["metadata"].update(last_modified="15/11/2026",
                            description="Grading rules and exercise parameters for the first scored Toa Payoh exercise.")
    data.update(revision="toa-payoh-rubric-r1", incident_duration_s=85, charging_decision_s=120,
                charging_observation_s=930, exercise_limit_s=960)
    data["rules"]["incident_time"].update(full_s=30, zero_s=60)
    data["rules"]["charging_follow_through"].update(charge_target_fraction=0.95, continue_s=120)
    return Rubric.model_validate(data)


def scored_evidence():
    """Build a complete attempt with tuned partial-credit boundaries for the original rubric."""
    actor = ScoredSessionActor(legacy_rubric(), session_id="points-test")
    snapshot = actor.generate_snapshot()
    plan = actor.assessment_plan
    snapshot.tick, snapshot.session.status = plan.ticks(plan.rubric.exercise_limit_s), "COMPLETED"
    route, driver = plan.rubric.route_id, plan.rubric.route_vehicle_id
    charger = plan.rubric.charging_vehicle_id
    events = []

    def add(kind, target, tick, **data):
        event = Event(event_id=uuid4().hex, event_sequence=len(events) + 1, event_type=kind,
            tick=tick, entity_type="VEHICLE", entity_id=target, entity_revision=1, source=snapshot.source, data=data)
        events.append(event)
        snapshot.event_sequence = event.event_sequence
        return event

    add("ROUTE_APPROVED", route, 0, target_vehicle_ids=[driver], directed_edge_ids=["example-closed-road"],
        edited={"duration_s": plan.reference_duration_s * 1.10, "energy_kwh": plan.reference_energy_kwh * 1.125})
    add("ASSESSMENT_CHARGING_OFFERED", charger, 0, option_id="charge-now")
    add("ASSESSMENT_CHARGING_ACTIONABLE", charger, 200)
    add("SERVICE_CONTINUED", charger, 1400, vehicle_id=charger)
    for stop in plan.required_stops:
        add("STOP_SERVED", driver, 3000, stop_id=stop)
    add("LOOP_COMPLETED", driver, 4000, route_id=route, duration_s=200)
    add("INCIDENT_DETECTED", SCORED_INCIDENT, 4100, active_until_tick=5800)
    add("OPTIONS_READY", SCORED_INCIDENT, 4100, options={"escalate": {"vehicle_id": driver, "validation": "PASS"}})
    add("INCIDENT_DECISION_RECORDED", SCORED_INCIDENT, 5000, vehicle_id=driver, action="ESCALATE")
    for stop in plan.required_stops:
        add("STOP_SERVED", driver, 7000, stop_id=stop)
    add("LOOP_COMPLETED", driver, 8900, route_id=route, duration_s=245)
    add("ASSESSMENT_CHARGING_OBSERVED", charger, plan.ticks(plan.rubric.charging_observation_s),
        minimum_kwh=7.3, minimum_in_service=3,
        reserve_kwh=6.0, capacity_kwh=40)
    return plan, snapshot, events, add


def test_partial_credit_and_config_only_amendment():
    plan, snapshot, events, _ = scored_evidence()
    grade = finalize(plan, snapshot, events)
    points = {item.rule_id: item.points for item in grade.criteria}
    assert points == {"route_approval": 10, "route_stops": 12, "route_time": 4, "route_energy": 2,
        "incident_choice": 20, "incident_time": 5, "incident_service": 5, "battery_reserve": 8,
        "fleet_coverage": 5, "charging_follow_through": 5}
    assert grade.points == 76 and grade.status == "PASS"
    revised = plan.rubric.model_dump()
    revised["revision"] = "updated-rubric"
    revised["rules"]["incident_time"].update(full_s=20, zero_s=45)
    updated = finalize(plan.model_copy(update={"rubric": Rubric.model_validate(revised)}), snapshot, events)
    assert updated.points == 71 and updated.status == "FAIL"
    assert finalize(plan, snapshot, events) == grade


def test_new_criterion_validates_and_scores_through_its_definition(monkeypatch):
    plan, snapshot, events, _ = scored_evidence()
    version = EVALUATOR_VERSIONS["assessment-v2"]

    def validate(_, rule):
        if rule.minimum_tick >= rule.maximum_tick:
            raise ValueError("Invalid approval window")

    def evaluate(evidence, rule):
        approval = evidence.approval
        assert approval is not None
        points = rule.maximum if rule.minimum_tick <= approval.tick <= rule.maximum_tick else 0
        return points, "Approved inside the configured window.", [approval]

    new_criterion = Criterion("Approval window", evaluate,
                              {"minimum_tick": lambda value: type(value) is int,
                               "maximum_tick": lambda value: type(value) is int}, validate)
    monkeypatch.setitem(EVALUATOR_VERSIONS, "assessment-test",
                        replace(version, criteria={**version.criteria, "approval_window": new_criterion}))
    config = load_rubric().model_dump()
    config.update(revision="rubric-test", evaluator_version="assessment-test")
    config["rules"]["route_approval"]["maximum"] -= 3
    config["rules"]["approval_window"] = {"maximum": 3, "minimum_tick": 0, "maximum_tick": 1_000}
    revised = plan.model_copy(update={"rubric": Rubric.model_validate(config)})
    grade = finalize(revised, snapshot, iter(events))
    criterion = grade.criteria[-1]
    assert (criterion.rule_id, criterion.label, criterion.points) == ("approval_window", "Approval window", 3)
    assert criterion.event_ids == [events[0].event_id]

    config["rules"]["approval_window"]["maximum_tick"] = 0
    with pytest.raises(ValueError, match="Invalid approval window"):
        Rubric.model_validate(config)


@pytest.mark.parametrize(("rule", "kind", "target", "tick", "detail"), [
    ("closed_road_entry", "ASSESSMENT_CLOSED_ROAD_ENTRY", "shuttle-01", 200,
     {"edge_id": "example-closed-road", "route_id": "route-peer-a", "closed_from_tick": 0, "incident_id": None}),
    ("incident_entry_without_clearance", "ASSESSMENT_CLOSED_ROAD_ENTRY", "shuttle-01", 4400,
     {"edge_id": "new-road-block", "route_id": "route-peer-a", "closed_from_tick": 4100, "incident_id": SCORED_INCIDENT}),
    ("preventable_battery_exhaustion", "BATTERY_EXHAUSTED", "shuttle-05", 18800, {}),
])
def test_automatic_fail_overrides_passing_points(rule, kind, target, tick, detail):
    plan, snapshot, events, add = scored_evidence()
    add(kind, target, tick, **detail)
    # Keep the fixture in journal order, as the store supplies it.
    events.sort(key=lambda event: event.tick)
    for index, event in enumerate(events, 1):
        event.event_sequence = index
    grade = finalize(plan, snapshot, events)
    assert grade.points == 76 and grade.status == "FAIL"
    assert [failure.rule_id for failure in grade.automatic_failures] == [rule]


def test_missed_and_unavailable_tasks_differ():
    plan, snapshot, events, add = scored_evidence()
    applicable = [e for e in events if e.event_type not in {"SERVICE_CONTINUED", "INCIDENT_DECISION_RECORDED"}]
    applicable.append(add("INCIDENT_UNRESOLVED", SCORED_INCIDENT, 5800))
    grade = finalize(plan, snapshot, applicable)
    assert grade.status == "FAIL"
    assert all(item.points == 0 for item in grade.criteria if item.rule_id.startswith(("incident_", "battery_", "fleet_", "charging_")))
    unavailable = [e for e in applicable if e.event_type != "ASSESSMENT_CHARGING_OFFERED"]
    assert finalize(plan, snapshot, unavailable).status == "INCOMPLETE"


def test_scheduled_charging_counts_the_observed_target_for_the_selected_booking():
    plan, snapshot, events, add = scored_evidence()
    events = [e for e in events if e.event_type != "SERVICE_CONTINUED"]
    events.extend((
        add("CHARGE_SCHEDULED", "charger-1", 1400, vehicle_id="shuttle-05", booking_id="chosen-booking"),
        add("ASSESSMENT_CHARGE_TARGET_REACHED", "shuttle-05", 16000, booking_id="other-booking", battery_kwh=38),
    ))

    def follow_through():
        events.sort(key=lambda event: event.tick)
        for index, event in enumerate(events, 1):
            event.event_sequence = index
        report = finalize(plan, snapshot, events)
        return next(item for item in report.criteria if item.rule_id == "charging_follow_through")

    assert follow_through().points == 0
    events.append(add("ASSESSMENT_CHARGE_TARGET_REACHED", "shuttle-05", 16500,
                      booking_id="chosen-booking", battery_kwh=38))
    assert follow_through().points == 5


def test_legacy_checkpoint_keeps_the_original_exercise_clock():
    rubric = legacy_rubric()
    actor = ScoredSessionActor(rubric, session_id="legacy-attempt")
    snapshot = actor.generate_snapshot()
    state = checkpoint(actor)
    state["assessment_plan"]["rubric"].pop("short_exercise")
    # Checkpoints saved before the countdown carried the duration only in the pinned rubric.
    state["workflow"]["domain"]["scenario"].pop("assessment_duration_s")
    snapshot.scenario.assessment_duration_s = None
    for field in ("assessment_stop_served", "incident_service_m", "trip_booking_id", "trip_m"):
        state["assessment_observations"].pop(field)
    restored = TrainingSessionService._restore_session(snapshot, state).actor
    base = IntegratedSessionActor(rubric.seed, session_id=actor.session_id).scenario_revision
    original_identity = json.dumps(state["assessment_plan"]["rubric"], sort_keys=True)
    original_revision = "scored-toa-payoh-" + sha256(f"{base}:{original_identity}".encode()).hexdigest()[:24]
    assert restored.scenario_revision == original_revision
    assert restored.assessment_plan.rubric.exercise_limit_s == 960


def test_cancelled_slot_keeps_previous_continue_decision():
    plan, snapshot, events, add = scored_evidence()
    add("CHARGE_SCHEDULED", "charger-1", 1450, vehicle_id="shuttle-05", booking_id="cancelled-slot")
    add("CHARGE_BOOKING_CANCELLED", "charger-1", 1500, vehicle_id="shuttle-05", booking_id="cancelled-slot")
    events.sort(key=lambda event: event.tick)
    for index, event in enumerate(events, 1):
        event.event_sequence = index
    grade = finalize(plan, snapshot, events)
    assert grade.points == 76 and grade.status == "PASS"


@pytest.mark.parametrize(("published", "crossing"), [(4400, 4200), (5000, 5000)])
def test_incident_safety_uses_crossing_time_and_committed_order(published, crossing):
    plan, snapshot, events, add = scored_evidence()
    add("ASSESSMENT_CLOSED_ROAD_ENTRY", "shuttle-01", published, edge_id="new-road-block",
        route_id="route-peer-a", crossing_tick=crossing, closed_from_tick=4100, incident_id=SCORED_INCIDENT)
    events.sort(key=lambda event: event.tick)
    for index, event in enumerate(events, 1):
        event.event_sequence = index
    assert finalize(plan, snapshot, events).automatic_failures == []


def test_closure_cache_expires_without_changing_the_previous_tick():
    actor = ScoredSessionActor(load_rubric(), session_id="closure-test")
    shuttle = actor.shuttles[actor.graded_route]
    closure = actor.domain.disruptions[0]
    edge, start, _ = next((edge, start, end) for edge, start, end in actor._route_roads(shuttle.route_ID)
                          if edge in closure.affected_edge_ids and start > SHUTTLE_FRONT_M + 1)
    shuttle.progress = start - SHUTTLE_FRONT_M - 1
    motion = {item.ID: item.progress for item in actor.shuttles}
    motion[shuttle.ID] += 2
    closure.active_until_tick = 2
    actor.current_tick = 1
    actor._observe_motion(motion)
    assert [event.data["edge_id"] for event in actor.pending_events] == [edge]
    staged = deepcopy(actor)
    assert staged.assessment_plan is actor.assessment_plan
    assert staged.observations is not actor.observations
    staged.current_tick = 2
    staged.pending_events.clear()
    staged._observe_motion(motion)
    assert staged.pending_events == []
    assert len(actor.pending_events) == 1


def test_two_minute_exercise_can_grade_all_three_tasks(tmp_path):
    """Close a real simulated attempt at two minutes using scheduled charging and escalation."""
    with TrainingStore(tmp_path / "scored.sqlite") as store:
        service = TrainingSessionService(store)
        created, actor = create_attempt(store)
        actor = approve_and_start(store, created, actor)
        baseline = store.snapshot(created.session_id)

        def apply_action(kind, target_type, target_id, revision, payload):
            nonlocal actor, baseline
            command = DispatcherCommand(contract_version="2.0", command_id=kind.lower(),
                idempotency_key=kind.lower(), session_id=baseline.session_id, command_type=kind,
                expected_epoch=baseline.session_epoch, expected_revision=revision, target_type=target_type,
                target_id=target_id, reason="Assessment decision", payload=payload)
            result, actor = service._submit_staged(actor, "trainee", command, False)
            assert result.status == "APPLIED"
            baseline = store.snapshot(created.session_id)

        offer = next(option for option in baseline.charge_options
                     if option.vehicle_id == "shuttle-05" and option.action == "CHARGE_NOW")
        charger = next(charger for charger in baseline.chargers if charger.charger_id == offer.charger_id)
        apply_action("SCHEDULE_CHARGE", "CHARGER", charger.charger_id, charger.aggregate_revision,
                     {"vehicle_id": offer.vehicle_id, "start_tick": actor.assessment_plan.ticks(10)})
        responded = False
        for elapsed_s in range(1, 121):
            for _ in range(actor.assessment_plan.ticks(1)):
                actor.tick()
            baseline = publish_workflow(store, actor, service._snapshot(actor, baseline, "RUNNING"), checkpoint, baseline)
            assert (baseline.session.status == "COMPLETED") == (elapsed_s == 120)
            candidate = next((candidate for candidate in baseline.candidates if candidate.incident_id == SCORED_INCIDENT
                              and candidate.vehicle_id == "shuttle-01" and candidate.action == "ESCALATE"
                              and candidate.validation == "PASS"), None)
            if candidate and not responded:
                incident = next(incident for incident in baseline.incidents if incident.incident_id == SCORED_INCIDENT)
                apply_action("APPROVE_INCIDENT_OPTION", "INCIDENT", incident.incident_id, incident.aggregate_revision,
                             {"candidate_id": candidate.candidate_id, "candidate_revision": candidate.revision})
                responded = True
        report = store.assessment(baseline.session_id, "trainee")
        assert responded and baseline.simulation_time_s == 120
        assert report.status == "PASS"
        points = {item.rule_id: item.points for item in report.criteria}
        assert points["route_stops"] == 12
        assert points["incident_service"] == 10
        assert points["charging_follow_through"] == 5
