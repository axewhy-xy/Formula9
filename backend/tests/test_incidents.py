"""BE-M2-05 incident acceptance cases against the committed directed map.

The authored scenario has no fleet actor: its incident is a closure on shuttle-01's route that holds it where
it was detected, and the tests stand in for the actor, moving the fleet and (for the shuttles it would find
exposed) giving the incident's record the exposures the actor would work out.
"""

import json
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from contracts.v2.models import DispatcherCommand, Disruption, FullSnapshot, Provenance
from src.api.routes.training_v2 import get_actor_id, get_training_store
from src.app import app
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.routing.road_graph import get_road_graph_repository
from src.services.incidents import (
    LATE_RESPONSE_SHARE, RECORD_FIELDS, IncidentService, delay_share, detour_route_id, detouring,
    get_incident_service, holding_at_stop, is_detour_of, response_share,
)
from src.services.route_edit import RouteEditService
from src.services.scene_repository import get_scene_repository


ROOT = Path(__file__).resolve().parents[2]
INCIDENT = "incident-m2-obstruction"


@pytest.fixture(scope="module")
def service():
    return IncidentService(RouteEditService(get_road_graph_repository()))


def initial(service, tick=238):
    raw = json.loads((ROOT / "contracts/v2/fixtures/snapshot.json").read_text(encoding="utf-8"))
    road = service.routes._fixture
    scene = get_scene_repository().metadata()
    raw.update(tick=tick, simulation_time_s=tick * 0.05, event_sequence=0,
               scene_revision=scene.scene_revision, transform_revision=scene.transform_revision,
               incidents=[], candidates=[], disruptions=[], energy_forecasts=[], charge_options=[])
    raw["scenario"].update(scenario_id=service.config["scenario_id"],
                           revision_id=service.config["fixture_id"],
                           rubric_revision=service.config["rubric_revision"],
                           scene_revision=scene.scene_revision, transform_revision=scene.transform_revision)
    raw["session"].update(status="RUNNING", scenario_revision_id=service.config["fixture_id"])
    raw["routes"][0].update(route_id=road["route_id"], revision=1,
                             scene_revision=scene.scene_revision,
                             directed_edge_ids=road["original_directed_edge_ids"],
                             required_stop_ids=list(road["required_stops"]))
    for vehicle in raw["vehicles"]:
        vehicle.update(route_id=road["route_id"], route_revision=1,
                       service_state="IN_SERVICE", speed_mps=5.0)
    return FullSnapshot.model_validate(raw)


def advance(service, store, ticks=2):
    staged = store.snapshot("session-001")
    staged.tick += ticks
    staged.simulation_time_s = staged.tick * 0.05
    # Deliberately propose movement for every vehicle: incident hook must hold only its target.
    for vehicle in staged.vehicles:
        vehicle.position.x_m += 5.0 * ticks * 0.05
    return service.publish(store, staged)


@pytest.fixture
def detected(tmp_path, service):
    with TrainingStore(tmp_path / "incidents.sqlite") as store:
        store.create_session(initial(service))
        advance(service, store)
        yield store


def command(store, action="KEEP_ROUTE", vehicle_id="shuttle-01", **changes):
    snapshot = store.snapshot("session-001")
    incident = snapshot.incidents[0]
    option = next(c for c in snapshot.candidates if c.action == action and c.vehicle_id == vehicle_id)
    values = dict(contract_version="2.0", command_id="decision-1", idempotency_key="decision-key-1",
                  session_id=snapshot.session_id, command_type="APPROVE_INCIDENT_OPTION",
                  expected_epoch=snapshot.session_epoch, expected_revision=incident.aggregate_revision,
                  target_type="INCIDENT", target_id=incident.incident_id,
                  payload={"candidate_id": option.candidate_id}, reason="Keep the shuttle safe and serve its stops")
    values.update(changes)
    return DispatcherCommand(**values)


def events(store):
    return [e.event_type for e in store.replay("session-001", 0).events]


def test_scripted_obstruction_stops_one_shuttle_and_emits_valid_options(detected, service):
    snapshot = detected.snapshot("session-001")
    incident = snapshot.incidents[0]
    assert incident.state == "OPTIONS_READY" and incident.detected_tick == 240
    assert incident.clears_at_tick == 240 + service.config["clear_after_ticks"] and incident.responses == []
    assert snapshot.vehicles[0].service_state == "AWAITING_GUIDANCE"
    assert snapshot.vehicles[0].speed_mps == 0.0
    assert snapshot.vehicles[0].position.x_m == initial(service).vehicles[0].position.x_m
    # A shuttle held where it was detected keeps its route, takes a detour or gets assistance; it has no stop
    # to hold at
    assert [c.action for c in snapshot.candidates] == ["KEEP_ROUTE", "ROAD_DETOUR", "ESCALATE"]
    assert all(c.vehicle_id == "shuttle-01" and "-shuttle-01-" in c.candidate_id for c in snapshot.candidates)
    assert all(c.validation == "PASS" and c.assumptions and c.expires_at_tick > snapshot.tick for c in snapshot.candidates)
    assert all(c.dependency_revisions == {INCIDENT: 2} for c in snapshot.candidates)
    assert events(detected) == ["INCIDENT_DETECTED", "OPTIONS_READY"]
    before = snapshot.model_copy(deep=True)
    after = advance(service, detected, 20)
    assert after.vehicles[0].position == before.vehicles[0].position
    assert all(after.vehicles[i].position.x_m > before.vehicles[i].position.x_m for i in range(1, 5))
    assert after.chargers == before.chargers and after.energy_forecasts == before.energy_forecasts


@pytest.mark.parametrize("service_state,on_authored_route", [
    ("DWELLING", True), ("OUT_OF_SERVICE", True), ("IN_SERVICE", False),
])
def test_trigger_defers_until_shuttle_is_eligible(tmp_path, service, service_state, on_authored_route):
    with TrainingStore(tmp_path / "deferred-trigger.sqlite") as store:
        snapshot = initial(service)
        shuttle = snapshot.vehicles[0]
        shuttle.service_state = service_state
        if not on_authored_route:
            other_route = snapshot.routes[0].model_copy(update={"route_id": "other-route"})
            snapshot.routes.append(other_route)
            shuttle.route_id = other_route.route_id
        before = store.create_session(snapshot)
        for _ in range(2):
            deferred = advance(service, store)
            assert store.snapshot("session-001") == deferred
            assert deferred.tick == before.tick + 2
            assert deferred.incidents == deferred.candidates == deferred.disruptions == []
            assert deferred.event_sequence == 0 and deferred.source == before.source
            assert deferred.vehicles[0].service_state == service_state
            assert all(after.position.x_m > prior.position.x_m
                       for after, prior in zip(deferred.vehicles[1:], before.vehicles[1:]))
            before = deferred

        staged = store.snapshot("session-001")
        staged.vehicles[0].service_state = "IN_SERVICE"
        staged.vehicles[0].route_id = service.config["route_id"]
        staged.tick += 2
        staged.simulation_time_s = staged.tick * service.config["step_s"]
        detected = service.publish(store, staged)
        assert detected.incidents[0].detected_tick == staged.tick > service.config["trigger_tick"]
        assert detected.vehicles[0].service_state == "AWAITING_GUIDANCE"
        assert detected.vehicles[0].speed_mps == 0.0
        assert detected.disruptions[0].active_until_tick == staged.tick + service.config["clear_after_ticks"]
        advance(service, store)
        assert len(store.snapshot("session-001").incidents) == 1
        assert events(store) == ["INCIDENT_DETECTED", "OPTIONS_READY"]


@pytest.mark.parametrize("action", ["KEEP_ROUTE", "ESCALATE", "ROAD_DETOUR"])
@pytest.mark.parametrize("validation", ["FAIL", "UNKNOWN"])
def test_failed_or_unknown_options_are_rejected_without_scoring(tmp_path, service, monkeypatch, action, validation):
    if validation == "FAIL":
        monkeypatch.setitem(service.routes._fixture, "road_width_m", 0.1)
    else:
        monkeypatch.delitem(service.routes._fixture, "road_width_m")
    with TrainingStore(tmp_path / "invalid-option.sqlite") as store:
        store.create_session(initial(service))
        snapshot = advance(service, store)
        assert next(c for c in snapshot.candidates if c.action == action).validation == validation
        request = command(store, action)
        with pytest.raises(TrainingStoreError) as error:
            service.preview(store, "session-001", request)
        assert error.value.code == "UNKNOWN_CONSTRAINT"
        assert store.snapshot("session-001") == snapshot
        assert store.incident_attempts("session-001", INCIDENT) == []

        result = service.decide(store, "actor-a", request)
        assert result.status == "REJECTED" and result.failure_code == "UNKNOWN_CONSTRAINT"
        assert store.command_status("session-001", "actor-a", request.idempotency_key) == result
        debrief = service.debrief(store, "session-001", INCIDENT)
        assert debrief["state"] == "OPTIONS_READY" and debrief["assessment"] is None
        assert debrief["decisions"] == [] and debrief["attempts"][0]["status"] == "REJECTED"
        assert events(store) == ["INCIDENT_DETECTED", "OPTIONS_READY", "COMMAND_REJECTED"]


@pytest.mark.parametrize("action", ["KEEP_ROUTE", "ESCALATE", "ROAD_DETOUR"])
def test_http_missing_baseline_rejects_without_stranding_command(tmp_path, service, action):
    with TrainingStore(tmp_path / "missing-baseline.sqlite") as store:
        snapshot = initial(service)
        snapshot.routes[0].directed_edge_ids = ["unavailable-road-segment"]
        store.create_session(snapshot)
        detected = advance(service, store)
        evidence = next(e for e in store.entity_events("session-001", INCIDENT, detected.event_sequence)
                        if e.event_type == "OPTIONS_READY")
        option = next(o for o in evidence.data["options"].values() if o["action"] == action)
        assert option["baseline"] is None
        if action != "ROAD_DETOUR":
            assert option["estimated_consequence"] is None
        request = command(store, action).model_dump(mode="json")
        app.dependency_overrides[get_training_store] = lambda: store
        app.dependency_overrides[get_actor_id] = lambda: "actor-a"
        app.dependency_overrides[get_incident_service] = lambda: service
        try:
            with TestClient(app) as client:
                base = "/api/v2/sessions/session-001"
                preview = client.post(base + "/incident-previews", json=request)
                assert preview.status_code == 400 and preview.json()["error"]["code"] == "UNKNOWN_CONSTRAINT"
                response = client.post(base + "/commands", json=request)
                assert response.status_code == 202
                result = response.json()
                assert result["status"] == "REJECTED" and result["failure_code"] == "UNKNOWN_CONSTRAINT"
                status = client.get(base + "/commands/" + request["idempotency_key"])
                assert status.status_code == 200 and status.json() == result
                assert client.post(base + "/commands", json=request).json() == result
                debrief = service.debrief(store, "session-001", INCIDENT)
                assert debrief["decisions"] == [] and debrief["assessment"] is None
                assert len(debrief["attempts"]) == 1 and debrief["attempts"][0]["status"] == "REJECTED"
        finally:
            app.dependency_overrides.clear()


@pytest.mark.parametrize("action,state", [("KEEP_ROUTE", "WAITING"), ("ESCALATE", "ESCALATED")])
def test_keep_route_and_simulated_escalation_resolve_on_authored_clock(detected, service, action, state):
    advance(service, detected, 20)
    request = command(detected, action)
    assert service.decide(detected, "actor-a", request).status == "APPLIED"
    snapshot = detected.snapshot("session-001")
    incident = snapshot.incidents[0]
    assert incident.state == state and snapshot.vehicles[0].speed_mps == 0.0
    assert [(r.vehicle_id, r.action, r.decided_tick, r.before_arrival) for r in incident.responses] == \
        [("shuttle-01", action, 260, True)]
    assert snapshot.candidates == [] and incident.candidate_ids == []
    expected_clear = 440 if action == "KEEP_ROUTE" else 360
    assert snapshot.disruptions[0].active_until_tick == incident.clears_at_tick == expected_clear
    # Nobody else is to be decided for, so no options are reissued while it waits
    advance(service, detected, 2)
    assert detected.snapshot("session-001").candidates == []
    advance(service, detected, expected_clear - detected.snapshot("session-001").tick)
    after = detected.snapshot("session-001")
    assert after.incidents[0].state == "RESOLVED" and after.vehicles[0].service_state == "IN_SERVICE"
    assert after.incidents[0].responses[0].completed_tick == expected_clear
    decision = service.debrief(detected, "session-001", INCIDENT)["decisions"][0]
    assert decision["reason"] == request.reason and decision["response_time_s"] == 1.0
    assert decision["vehicle_id"] == "shuttle-01" and decision["before_arrival"] is True
    assert decision["wall_response_time_s"] >= 0
    assert decision["estimated_consequence"]["energy_kwh"] > decision["baseline"]["energy_kwh"]
    assert decision["estimated_consequence"]["stop_coverage"] == {"stop-terminal": True}
    assert decision["assessment"]["total"] <= 100
    outcome = service.debrief(detected, "session-001", INCIDENT)["outcome"]
    assert outcome["missed_vehicle_ids"] == [] and outcome["responses"][0]["action"] == action


def test_detour_applies_only_to_affected_shuttle_and_actor_completes_it(detected, service):
    before = detected.snapshot("session-001")
    assert service.decide(detected, "actor-a", command(detected, "ROAD_DETOUR")).status == "APPLIED"
    after = detected.snapshot("session-001")
    shuttle = after.vehicles[0]
    assert shuttle.service_state == "DETOURING" and after.incidents[0].state == "EXECUTING"
    assert shuttle.route_id == detour_route_id(INCIDENT, "shuttle-01") and is_detour_of(shuttle.route_id, INCIDENT)
    assert detouring(after.incidents, "shuttle-01") and not detouring(after.incidents, "shuttle-02")
    assert after.vehicles[1:] == before.vehicles[1:]
    assert after.routes[0] == before.routes[0]
    assert after.routes[-1].directed_edge_ids == service.routes._fixture["detour_directed_edge_ids"]
    assert after.routes[-1].required_stop_ids == before.routes[0].required_stop_ids
    assert after.vehicles[0].battery_kwh == before.vehicles[0].battery_kwh
    after.vehicles[0].service_state = "IN_SERVICE"  # Fleet actor has reached the route destination.
    after.tick += 2
    after.simulation_time_s = after.tick * 0.05
    driven = service.publish(detected, after)
    # The detour is driven; the closure is still there, and settles the incident when it clears
    assert driven.incidents[0].state == "WAITING" and driven.incidents[0].responses[0].completed_tick == after.tick
    assert not detouring(driven.incidents, "shuttle-01")
    assert events(detected)[-1] == "INCIDENT_RESPONSE_COMPLETED"
    advance(service, detected, 440 - driven.tick)
    assert detected.snapshot("session-001").incidents[0].state == "RESOLVED"


def test_preview_branches_from_committed_checkpoint_without_live_mutation(detected, service):
    before = detected.snapshot("session-001")
    replay = detected.replay("session-001", 0)
    preview = service.preview(detected, "session-001", command(detected, "ROAD_DETOUR"))
    assert preview["base_checkpoint_id"] == before.snapshot_id
    assert preview["base_tick"] == before.tick and preview["base_event_sequence"] == before.event_sequence
    assert preview["source"]["kind"] == "TRAINING_SIMULATION" and preview["camera"] is None
    assert preview["snapshot"]["vehicles"][0]["service_state"] == "DETOURING"
    assert detected.snapshot("session-001") == before
    assert detected.replay("session-001", 0) == replay
    assert detected.incident_attempts("session-001", INCIDENT) == []


def test_ten_second_history_is_durable_and_never_uses_recorded_camera(tmp_path, service):
    path = tmp_path / "history.sqlite"
    with TrainingStore(path) as store:
        store.create_session(initial(service, tick=0))
        for _ in range(120):
            advance(service, store)
        history = service.history(store, "session-001", INCIDENT)
        assert history["complete"] and len(history["snapshots"]) == 101
        assert history["snapshots"][0]["tick"] == 40 and history["snapshots"][-1]["tick"] == 240
        assert history["camera"] is None
        assert all(s["source"]["kind"] == "HISTORICAL_SIMULATION" for s in history["snapshots"])
        history["snapshots"][0]["vehicles"][0]["position"]["x_m"] = -999.0
        assert store.history("session-001", 40, 240)[0].vehicles[0].position.x_m != -999.0
    with TrainingStore(path) as recovered:
        assert service.history(recovered, "session-001", INCIDENT)["complete"]


def test_missing_prehistory_is_explicit(detected, service):
    history = service.history(detected, "session-001", INCIDENT)
    assert history["complete"] is False and history["unavailable_reason"]
    assert history["camera"] is None and len(history["snapshots"]) == 2


@pytest.mark.parametrize("change", ["obstruction", "route", "rules", "scene_constraint", "expiry", "wrong_option", "blank_reason"])
def test_invalid_attempt_is_durably_rejected(detected, service, change):
    request = command(detected)
    staged = detected.snapshot("session-001")
    if change == "obstruction":
        staged.disruptions[0].aggregate_revision += 1
    elif change == "route":
        staged.routes[0].revision += 1
        for vehicle in staged.vehicles:
            vehicle.route_revision += 1
    elif change == "rules":
        staged.scenario.rules_revision = "rules-r2"
    elif change == "scene_constraint":
        staged.disruptions[0].affected_edge_ids = ["changed-obstacle-edge"]
    elif change == "expiry":
        staged.candidates[0].expires_at_tick = staged.tick
    elif change == "wrong_option":
        request.payload["candidate_id"] = "unsupported-lane-pass"
    else:
        request.reason = "   "
    staged.tick += 2
    staged.simulation_time_s = staged.tick * 0.05
    detected.commit_snapshot(staged)
    result = service.decide(detected, "actor-a", request)
    assert result.status == "REJECTED"
    assert result.failure_code in {"STALE_REVISION", "OPTION_EXPIRED", "INVALID_REFERENCE", "INVALID_PAYLOAD"}
    assert detected.snapshot("session-001").incidents[0].state == "OPTIONS_READY"
    assert detected.incident_attempts("session-001", INCIDENT)[0]["status"] == "REJECTED"
    assert detected.replay("session-001", 1).events[-1].event_type == "COMMAND_REJECTED"


def test_retry_and_restart_preserve_reason_score_and_single_application(detected, service):
    request = command(detected)
    rejected = request.model_copy(deep=True)
    rejected.command_id, rejected.idempotency_key = "bad-attempt", "bad-key"
    rejected.payload["candidate_id"] = "unavailable-option"
    assert service.decide(detected, "actor-a", rejected).status == "REJECTED"
    advance(service, detected, 20)
    applied = service.decide(detected, "actor-a", request)
    assert applied.status == "APPLIED"
    retry = request.model_copy(update={"command_id": "retry-id"})
    assert service.decide(detected, "actor-a", retry).result_event_id == applied.result_event_id
    data = service.debrief(detected, "session-001", INCIDENT)
    # The rejected attempt names no shuttle, so it counts against the shuttle decided for
    assert data["assessment"]["attempts"] == 2 and data["assessment"]["criteria"]["attempts"] == 8
    assert len(data["decisions"]) == 1
    path = detected.path
    detected.close()
    with TrainingStore(path) as recovered:
        assert service.decide(recovered, "actor-a", retry).result_event_id == applied.result_event_id
        assert service.debrief(recovered, "session-001", INCIDENT)["decisions"] == data["decisions"]
        conflict = request.model_copy(update={"reason": "A different reason"})
        with pytest.raises(TrainingStoreError) as error:
            service.decide(recovered, "actor-a", conflict)
        assert error.value.code == "IDEMPOTENCY_CONFLICT"


def test_score_is_deterministic_and_has_versioned_uncalibrated_criteria(detected, service):
    service.decide(detected, "actor-a", command(detected))
    first = service.debrief(detected, "session-001", INCIDENT)
    assert first == service.debrief(detected, "session-001", INCIDENT)
    score = first["assessment"]
    assert score["rubric_revision"] == service.config["rubric_revision"] == "m2-incident-provisional-r2"
    assert score["uncalibrated"] and score["provisional"] and score["scope"] == "INCIDENT_ONLY"
    assert score["vehicle_id"] == "shuttle-01" and first["assessments"] == {"shuttle-01": score}
    assert set(score["criteria"]) == {"safety_compliance", "response_time", "attempts", "service_coverage", "delay_avoided"}
    assert score["criteria"]["safety_compliance"] == 40
    assert score["criteria"]["response_time"] == 20
    assert score["criteria"]["service_coverage"] == 20
    assert score["criteria"]["attempts"] == 10
    # Keeping the route waits the whole closure out, when assistance would have halved it
    decision = first["decisions"][0]
    assert decision["keep_route_delay_s"] == decision["estimated_delay_s"] > decision["best_delay_s"]
    assert decision["delay_avoided_s"] == 0 and score["criteria"]["delay_avoided"] == 0
    assert score["total"] == round(sum(score["criteria"].values()), 2)
    assert sum(score["maximum_points"].values()) == 100


def test_the_least_delay_offered_earns_every_delay_avoided_point(detected, service):
    options = {o["action"]: o for o in next(e for e in detected.entity_events("session-001", INCIDENT, 10)
                                           if e.event_type == "OPTIONS_READY").data["options"].values()}
    best = min(options, key=lambda action: options[action]["estimated_delay_s"])
    service.decide(detected, "actor-a", command(detected, best))
    score = service.debrief(detected, "session-001", INCIDENT)["assessment"]
    assert score["criteria"]["delay_avoided"] == 10 and score["total"] == 100


def test_recorded_source_is_cut_at_earliest_branch(detected, service):
    staged = detected.snapshot("session-001")
    # Simulate an upstream actor retaining KITTI metadata after divergence.
    staged.source = Provenance(kind="RECORDED_SENSOR", recording_id="kitti", sequence_id="drive-1",
                               source_frame=7, source_timestamp="recorded-time", branch_tick=3)
    detected.commit_snapshot(staged.model_copy(update={"tick": 242, "simulation_time_s": 12.1}))
    after = advance(service, detected)
    assert after.source.kind == "TRAINING_SIMULATION" and after.source.branch_tick == 3
    assert all(v.provenance.source_frame is None for v in after.vehicles)
    assert service.preview(detected, "session-001", command(detected))["camera"] is None
    recorded = Provenance(kind="RECORDED_SENSOR", recording_id="kitti", sequence_id="drive-1",
                          source_frame=8, source_timestamp="recorded-time", branch_tick=3)
    after.tick += 2
    after.simulation_time_s = after.tick * 0.05
    after.source = recorded
    for vehicle in after.vehicles:
        vehicle.provenance = recorded
    detected.commit_snapshot(after)
    alternative = service.preview(detected, "session-001", command(detected))
    assert alternative["source"]["branch_tick"] == 3
    assert all(v["provenance"]["kind"] == "TRAINING_SIMULATION" and v["provenance"]["source_frame"] is None
               for v in alternative["snapshot"]["vehicles"])


def test_concurrent_publication_cannot_be_overwritten_by_decision(detected, service, monkeypatch):
    original = detected.apply
    def racing_apply(*args, **kwargs):
        advance(service, detected)
        return original(*args, **kwargs)
    monkeypatch.setattr(detected, "apply", racing_apply)
    result = service.decide(detected, "actor-a", command(detected, "ROAD_DETOUR"))
    assert result.status == "REJECTED" and result.failure_code == "STALE_REVISION"
    assert detected.snapshot("session-001").tick == 242
    assert detected.snapshot("session-001").incidents[0].state == "OPTIONS_READY"
    assert not service.debrief(detected, "session-001", INCIDENT)["decisions"]


def test_stale_actor_publication_is_rejected_after_decision(detected, service):
    stale = detected.snapshot("session-001")
    service.decide(detected, "actor-a", command(detected))
    stale.tick += 2
    stale.simulation_time_s = stale.tick * 0.05
    with pytest.raises(TrainingStoreError) as error:
        service.publish(detected, stale)
    assert error.value.code == "STALE_REVISION"
    assert detected.snapshot("session-001").incidents[0].state == "WAITING"


def test_http_incident_preview_decision_history_and_debrief(detected, service):
    app.dependency_overrides[get_training_store] = lambda: detected
    app.dependency_overrides[get_actor_id] = lambda: "actor-a"
    app.dependency_overrides[get_incident_service] = lambda: service
    try:
        with TestClient(app) as client:
            base = "/api/v2/sessions/session-001"
            request = command(detected).model_dump(mode="json")
            preview = client.post(base + "/incident-previews", json=request)
            assert preview.status_code == 200 and preview.json()["camera"] is None
            history = client.get(base + f"/incidents/{INCIDENT}/history")
            assert history.status_code == 200 and history.json()["source_mode"] == "HISTORICAL_SIMULATION"
            decision = client.post(base + "/commands", json=request)
            assert decision.status_code == 202 and decision.json()["status"] == "APPLIED"
            debrief = client.get(base + f"/incidents/{INCIDENT}/debrief")
            assert debrief.status_code == 200 and debrief.json()["assessment"]["provisional"]
            assert debrief.json()["responses"][0]["action"] == "KEEP_ROUTE"
            assert client.post("/api/v2/sessions/wrong-session/incident-previews", json=request).status_code == 404
            del app.dependency_overrides[get_actor_id]
            assert client.get(base + f"/incidents/{INCIDENT}/debrief").status_code == 403
    finally:
        app.dependency_overrides.clear()


def test_no_response_gets_durable_zero_score_and_is_not_auto_resolved(detected, service):
    advance(service, detected, 200)
    debrief = service.debrief(detected, "session-001", INCIDENT)
    assert debrief["state"] == "UNRESOLVED" and debrief["assessment"]["total"] == 0
    assert debrief["assessment"]["attempts"] == 0
    assert debrief["outcome"]["service_state"] == "IN_SERVICE"
    assert debrief["outcome"]["missed_vehicle_ids"] == ["shuttle-01"]
    assert [(r["vehicle_id"], r["action"], r["before_arrival"]) for r in debrief["responses"]] == \
        [("shuttle-01", "NO_DECISION", False)]
    assert debrief["assessments"]["shuttle-01"]["total"] == 0
    advance(service, detected)
    assert len(detected.snapshot("session-001").incidents) == 1
    assert events(detected).count("OPTIONS_READY") == 1
    assert events(detected)[-2:] == ["INCIDENT_RESPONSE_MISSED", "INCIDENT_UNRESOLVED"]


def test_escalation_revalidates_continuation_at_actual_release_time(tmp_path, service):
    with TrainingStore(tmp_path / "future-closure.sqlite") as store:
        snapshot = initial(service)
        snapshot.disruptions.append(Disruption(
            disruption_id="scheduled-closure", aggregate_revision=1,
            affected_edge_ids=service.routes._fixture["closed_directed_edge_ids"],
            active_from_tick=360, active_until_tick=600, reason="Later closure of the nominal road",
            provenance=snapshot.source,
        ))
        store.create_session(snapshot)
        detected = advance(service, store)
        options = {c.action: c for c in detected.candidates}
        assert options["KEEP_ROUTE"].validation == "FAIL"
        assert options["ESCALATE"].validation == "PASS"
        advance(service, store, 80)
        result = service.decide(store, "actor-a", command(store, "ESCALATE"))
        assert result.status == "REJECTED" and result.failure_code == "NO_FEASIBLE_ROUTE"


def test_failed_decision_transaction_retries_once_from_durable_input(detected, service, monkeypatch):
    original = TrainingStore._write_snapshot
    def crash(*args, **kwargs):
        raise RuntimeError("injected commit failure")
    request = command(detected)
    monkeypatch.setattr(TrainingStore, "_write_snapshot", staticmethod(crash))
    with pytest.raises(RuntimeError, match="injected"):
        service.decide(detected, "actor-a", request)
    assert detected.command_status("session-001", "actor-a", request.idempotency_key).status == "QUEUED"
    assert len(detected.replay("session-001", 0).events) == 2
    monkeypatch.setattr(TrainingStore, "_write_snapshot", staticmethod(original))
    assert service.decide(detected, "actor-a", request).status == "APPLIED"
    debrief = service.debrief(detected, "session-001", INCIDENT)
    assert len(debrief["decisions"]) == 1 and debrief["assessment"]["attempts"] == 1


def test_recovery_does_not_silently_execute_a_queued_incident(detected, service):
    request = command(detected)
    assert detected.accept("actor-a", request).status == "QUEUED"
    path = detected.path
    detected.close()
    with TrainingStore(path) as recovered:
        status = service.decide(recovered, "actor-a", request)
        assert status.status == "NEEDS_RECONFIRMATION"
        assert recovered.snapshot("session-001").incidents[0].state == "OPTIONS_READY"
        assert service.debrief(recovered, "session-001", INCIDENT)["decisions"] == []
        paused = recovered.snapshot("session-001")
        resume = DispatcherCommand(
            contract_version="2.0", command_id="resume", idempotency_key="resume-key",
            session_id=paused.session_id, command_type="RESUME", expected_epoch=paused.session_epoch,
            expected_revision=paused.session.aggregate_revision, target_type="SESSION",
            target_id=paused.session_id, payload={},
        )
        recovered.accept("actor-a", resume)
        paused.session.status = "RUNNING"
        paused.session.aggregate_revision += 1
        recovered.apply(resume.command_id, paused)
        fresh = advance(service, recovered)
        # The recovery's new epoch made the options stale: they are reissued
        assert fresh.incidents[0].aggregate_revision == 3
        reconfirmed = command(recovered, command_id="reconfirmed", idempotency_key="reconfirmed-key")
        assert service.decide(recovered, "actor-a", reconfirmed).status == "APPLIED"
        decision = service.debrief(recovered, "session-001", INCIDENT)["decisions"][0]
        assert decision["response_time_s"] == 0.1  # Refresh did not reset the original detection clock.


def test_history_reports_sampling_gaps_even_with_full_time_endpoints(tmp_path, service):
    with TrainingStore(tmp_path / "gap.sqlite") as store:
        store.create_session(initial(service, tick=40))
        advance(service, store, 200)
        history = service.history(store, "session-001", INCIDENT)
        assert history["complete"] is False and len(history["snapshots"]) == 2
        assert history["unavailable_reason"]


def test_options_refresh_after_obstruction_change_and_old_approval_is_rejected(detected, service):
    old_request = command(detected)
    staged = detected.snapshot("session-001")
    staged.disruptions[0].aggregate_revision += 1
    staged.tick += 2
    staged.simulation_time_s = staged.tick * 0.05
    detected.commit_snapshot(staged)
    refreshed = advance(service, detected)
    assert refreshed.incidents[0].aggregate_revision == 3
    assert len(refreshed.candidates) == 3
    assert all("-r3-" in candidate.candidate_id for candidate in refreshed.candidates)
    assert service.decide(detected, "actor-a", old_request).failure_code == "STALE_REVISION"
    fresh_request = command(detected, command_id="fresh-command", idempotency_key="fresh-key")
    assert service.decide(detected, "actor-a", fresh_request).status == "APPLIED"


def test_keep_route_delay_counts_from_the_decision_and_response_time_from_detection(detected, service):
    early = service.preview(detected, "session-001", command(detected))["decision"]
    advance(service, detected, 100)
    late = service.preview(detected, "session-001", command(detected))["decision"]
    assert late["response_time_s"] == 5.0
    # A held shuttle has waited since detection: its delay is the wait still to come plus the wait so far
    assert late["estimated_additional_service_delay_s"] == early["estimated_additional_service_delay_s"] == 10.0
    assert late["estimated_additional_energy_kwh"] == early["estimated_additional_energy_kwh"]
    assert late["keep_route_delay_s"] < early["keep_route_delay_s"]
    # Both came well inside the full-credit window, before the shuttle reached the closure
    assert service._score(late, 1)["criteria"]["response_time"] == service._score(early, 1)["criteria"]["response_time"] == 20


# ----- Several shuttles, as the actor finds them exposed -----

def record(service, **changes):
    """The authored incident's record, as the actor would checkpoint it, with changes."""
    base = {key: value for key, value in service.config.items() if key in RECORD_FIELDS}
    base.update(changes)
    return base


EXPOSED = [{"vehicle_id": "shuttle-02", "distance_m": 120.0, "eta_s": 24.0, "keep_route_delay_s": 9.0,
            "hold_stop": {"stop_id": "stop-terminal", "progress_m": 40.0, "distance_m": 20.0, "eta_s": 4.0}}]


def publish(instance, store, ticks=2):
    staged = store.snapshot("session-001")
    staged.tick += ticks
    staged.simulation_time_s = staged.tick * 0.05
    for vehicle in staged.vehicles:
        vehicle.position.x_m += 5.0 * ticks * 0.05
    return instance.publish(store, staged)


@pytest.fixture
def shared(tmp_path, service):
    """The closure, also reached by shuttle-02, which drives on towards it."""
    with TrainingStore(tmp_path / "shared.sqlite") as store:
        store.create_session(initial(service))
        publish(service.instance(record(service, exposures=EXPOSED)), store)
        yield store


def test_every_exposed_shuttle_is_offered_its_own_options(shared, service):
    snapshot = shared.snapshot("session-001")
    incident = snapshot.incidents[0]
    assert incident.state == "OPTIONS_READY" and incident.exposed_vehicle_ids == ["shuttle-02"]
    assert incident.exposures[0].keep_route_delay_s == 9.0
    by_vehicle = {}
    for candidate in snapshot.candidates:
        by_vehicle.setdefault(candidate.vehicle_id, []).append(candidate.action)
    # The held shuttle has no stop to hold at; the one driving on does
    assert by_vehicle == {"shuttle-01": ["KEEP_ROUTE", "ROAD_DETOUR", "ESCALATE"],
                          "shuttle-02": ["KEEP_ROUTE", "ROAD_DETOUR", "ESCALATE", "HOLD_AT_STOP"]}
    evidence = next(e for e in shared.entity_events("session-001", INCIDENT, snapshot.event_sequence)
                    if e.event_type == "OPTIONS_READY")
    options = {(o["vehicle_id"], o["action"]): o for o in evidence.data["options"].values()}
    assert options[("shuttle-02", "KEEP_ROUTE")]["estimated_delay_s"] == 9.0
    assert options[("shuttle-02", "HOLD_AT_STOP")]["hold_stop"]["stop_id"] == "stop-terminal"
    # It waits at its stop until the closure clears, 4 s from it, instead of in the queue
    assert options[("shuttle-02", "HOLD_AT_STOP")]["estimated_delay_s"] == pytest.approx(10.0 - 4.0)


def test_assistance_is_requested_once_and_each_shuttle_is_decided_for_on_its_own(shared, service):
    instance = service.instance(record(service, exposures=EXPOSED))
    assert instance.decide(shared, "actor-a", command(shared, "ESCALATE", "shuttle-02")).status == "APPLIED"
    snapshot = shared.snapshot("session-001")
    incident = snapshot.incidents[0]
    # shuttle-01 is still to be decided for; everyone's options are reissued at the next publication
    assert incident.state == "OPTIONS_READY" and snapshot.candidates == []
    assert incident.clears_at_tick == snapshot.disruptions[0].active_until_tick == 240 + 100
    reissued = publish(instance, shared)
    assert {(c.vehicle_id, c.action) for c in reissued.candidates} == {
        ("shuttle-01", "KEEP_ROUTE"), ("shuttle-01", "ROAD_DETOUR")}
    request = command(shared, "KEEP_ROUTE", "shuttle-01", command_id="second", idempotency_key="second-key")
    assert instance.decide(shared, "actor-a", request).status == "APPLIED"
    decided = shared.snapshot("session-001").incidents[0]
    assert decided.state == "ESCALATED"
    assert [(r.vehicle_id, r.action) for r in decided.responses] == [("shuttle-02", "ESCALATE"), ("shuttle-01", "KEEP_ROUTE")]
    publish(instance, shared, 340 - shared.snapshot("session-001").tick)
    debrief = service.debrief(shared, "session-001", INCIDENT)
    assert debrief["state"] == "RESOLVED" and set(debrief["assessments"]) == {"shuttle-01", "shuttle-02"}
    assert all(a["attempts"] == 1 for a in debrief["assessments"].values())


def test_a_second_decision_for_the_same_shuttle_is_refused(shared, service):
    instance = service.instance(record(service, exposures=EXPOSED))
    stale = command(shared, "HOLD_AT_STOP", "shuttle-02")
    assert instance.decide(shared, "actor-a", command(shared, "KEEP_ROUTE", "shuttle-02",
                                                      command_id="first", idempotency_key="first-key")).status == "APPLIED"
    publish(instance, shared)
    assert all(c.vehicle_id == "shuttle-01" for c in shared.snapshot("session-001").candidates)
    result = instance.decide(shared, "actor-a", stale)
    assert result.status == "REJECTED"


def test_holding_at_a_stop_holds_until_the_incident_clears(shared, service):
    instance = service.instance(record(service, exposures=EXPOSED))
    assert instance.decide(shared, "actor-a", command(shared, "HOLD_AT_STOP", "shuttle-02")).status == "APPLIED"
    incident = shared.snapshot("session-001").incidents[0]
    assert holding_at_stop([incident], "shuttle-02", 300, "stop-terminal") and not holding_at_stop([incident], "shuttle-02", 440, "stop-terminal")
    assert not holding_at_stop([incident], "shuttle-02", 300, "another-stop")
    assert incident.responses[0].hold_stop_id == "stop-terminal"
    assert not holding_at_stop([incident], "shuttle-01", 300, "stop-terminal")
    decision = service.debrief(shared, "session-001", INCIDENT)["decisions"][0]
    assert decision["action"] == "HOLD_AT_STOP" and decision["estimated_delay_s"] < decision["keep_route_delay_s"]
    assert decision["delay_avoided_s"] > 0


def test_a_shuttle_that_reaches_the_incident_undecided_has_decided(shared, service):
    arrived = [{**EXPOSED[0], "distance_m": 0.0, "eta_s": 0.0}]
    instance = service.instance(record(service, exposures=arrived))
    publish(instance, shared)
    snapshot = shared.snapshot("session-001")
    incident = snapshot.incidents[0]
    assert [(r.vehicle_id, r.action, r.before_arrival) for r in incident.responses] == [("shuttle-02", "NO_DECISION", False)]
    missed = next(e for e in shared.entity_events("session-001", INCIDENT, snapshot.event_sequence)
                  if e.event_type == "INCIDENT_RESPONSE_MISSED")
    assert missed.data["vehicle_id"] == "shuttle-02" and missed.data["assessment"]["total"] == 0
    # It can still be decided for, late, for half the response points
    assert incident.state == "OPTIONS_READY" and any(c.vehicle_id == "shuttle-02" for c in snapshot.candidates)
    late = command(shared, "KEEP_ROUTE", "shuttle-02")
    assert instance.decide(shared, "actor-a", late).status == "APPLIED"
    response = next(r for r in shared.snapshot("session-001").incidents[0].responses if r.vehicle_id == "shuttle-02")
    assert response.action == "KEEP_ROUTE" and response.before_arrival is False
    score = service.debrief(shared, "session-001", INCIDENT)["assessments"]["shuttle-02"]
    assert score["criteria"]["response_time"] == round(20 * LATE_RESPONSE_SHARE, 2)


def test_an_incident_a_shuttle_reached_undecided_is_unresolved(shared, service):
    instance = service.instance(record(service, exposures=[{**EXPOSED[0], "distance_m": 0.0, "eta_s": 0.0}]))
    publish(instance, shared)
    assert instance.decide(shared, "actor-a", command(shared, "KEEP_ROUTE", "shuttle-01")).status == "APPLIED"
    publish(instance, shared, 440 - shared.snapshot("session-001").tick)
    debrief = service.debrief(shared, "session-001", INCIDENT)
    assert debrief["state"] == "UNRESOLVED" and debrief["outcome"]["missed_vehicle_ids"] == ["shuttle-02"]
    assert debrief["assessments"]["shuttle-01"]["total"] > 0 and debrief["assessments"]["shuttle-02"]["total"] == 0


def test_a_shuttle_no_longer_exposed_has_its_options_withdrawn(shared, service):
    publish(service.instance(record(service, exposures=[])), shared)
    snapshot = shared.snapshot("session-001")
    assert {c.vehicle_id for c in snapshot.candidates} == {"shuttle-01"}
    assert snapshot.incidents[0].exposed_vehicle_ids == []
    assert "INCIDENT_EXPOSURE_CHANGED" in events(shared)


def test_an_incident_from_before_per_shuttle_decisions_keeps_its_decision(detected, service):
    staged = detected.snapshot("session-001")
    incident = staged.incidents[0]
    incident.state, incident.candidate_ids, incident.clears_at_tick = "WAITING", [], None
    staged.candidates = []
    staged.tick += 2
    staged.simulation_time_s = staged.tick * 0.05
    detected.commit_snapshot(staged)
    upgraded = advance(service, detected).incidents[0]
    assert [(r.vehicle_id, r.action) for r in upgraded.responses] == [("shuttle-01", "KEEP_ROUTE")]
    assert upgraded.state == "WAITING" and upgraded.candidate_ids == []


@pytest.mark.parametrize("fraction,before,share", [(1.0, True, 1.0), (1.0, False, LATE_RESPONSE_SHARE), (0.4, False, 0.2)])
def test_a_decision_after_arrival_earns_half_the_response_points(fraction, before, share):
    assert response_share(fraction, before) == pytest.approx(share)


@pytest.mark.parametrize("keep,chosen,best,share", [
    (30.0, 5.0, 5.0, 1.0),     # the least delay on offer
    (30.0, 30.0, 5.0, 0.0),    # no better than keeping the route
    (30.0, 17.5, 5.0, 0.5),    # half way
    (30.0, 40.0, 5.0, 0.0),    # worse than keeping the route
    (10.0, 10.0, 10.0, 1.0),   # nothing could have been avoided
])
def test_delay_avoided_compares_the_choice_with_keeping_the_route_and_the_best_option(keep, chosen, best, share):
    assert delay_share(keep, chosen, best) == pytest.approx(share)
