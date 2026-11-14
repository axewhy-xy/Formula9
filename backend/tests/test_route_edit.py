"""BE-M2-04: curated detour validation and durable approval."""

import json
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from threading import Barrier, Event

import pytest
from fastapi.testclient import TestClient

from contracts.v2.models import DispatcherCommand, FullSnapshot
from contracts.v2.route_edit import RouteApprovalRequest, RouteEditPreview, RouteEditRequest
from src.api.routes.training_v2 import get_actor_id, get_training_store
from src.app import app
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.routing.road_graph import get_road_graph_repository
from src.services.route_edit import RouteEditService, get_route_edit_service
from src.services.scene_repository import get_scene_repository


ROOT = Path(__file__).resolve().parents[2]
ROUTE_FIXTURE = ROOT / "backend" / "fixtures" / "m2_route_detour.json"
SNAPSHOT_FIXTURE = ROOT / "contracts" / "v2" / "fixtures" / "snapshot.json"


@pytest.fixture(scope="module")
def service() -> RouteEditService:
    return RouteEditService(get_road_graph_repository())


def initial() -> FullSnapshot:
    raw = json.loads(SNAPSHOT_FIXTURE.read_text(encoding="utf-8"))
    config = json.loads(ROUTE_FIXTURE.read_text(encoding="utf-8"))
    scene = get_scene_repository().metadata()
    raw["snapshot_id"] = "m2-pretrip-snapshot"
    raw["session"]["status"] = "READY"
    raw["tick"] = 0
    raw["simulation_time_s"] = 0.0
    raw["event_sequence"] = 0
    raw["scene_revision"] = scene.scene_revision
    raw["transform_revision"] = scene.transform_revision
    raw["scenario"]["scene_revision"] = scene.scene_revision
    raw["scenario"]["transform_revision"] = scene.transform_revision
    raw["routes"][0].update(route_id=config["route_id"], revision=1,
                             scene_revision=scene.scene_revision,
                             directed_edge_ids=config["original_directed_edge_ids"],
                             required_stop_ids=list(config["required_stops"]))
    raw["disruptions"][0].update(disruption_id="closure-m2", active_from_tick=0,
                                  affected_edge_ids=config["closed_directed_edge_ids"])
    for vehicle in raw["vehicles"]:
        vehicle["route_id"] = config["route_id"]
        vehicle["route_revision"] = 1
        vehicle["service_state"] = "STANDBY"
        vehicle["speed_mps"] = 0.0
    raw["incidents"] = []
    raw["candidates"] = []
    # A charge offer that the concurrent RESERVE_CHARGER probe can choose.
    raw["charge_options"] = [{
        "option_id": "charge-option-shuttle-01-charger-1", "vehicle_id": "shuttle-01", "revision": 1,
        "action": "CHARGE_NOW", "charger_id": "charger-1", "validation": "PASS", "expires_at_tick": 100,
        "dependency_revisions": {"shuttle-01": 1, "charger-1": 1}, "out_of_service_s": 1200.0,
        "battery_after_kwh": 40.0, "loops_after": None, "assumptions": [], "is_estimate": True}]
    raw["charge_option_revisions"] = {"charge-option-shuttle-01-charger-1": 1}
    return FullSnapshot.model_validate(raw)


def draft(**changes) -> RouteEditRequest:
    values = dict(route_id="route-m2-detour", expected_route_revision=1, draft_revision=3)
    values.update(changes)
    return RouteEditRequest(**values)


def approval(preview, **changes) -> RouteApprovalRequest:
    values = dict(preview_id=preview.preview_id, preview_revision=preview.preview_revision,
                  command_id="m2-command-1", idempotency_key="m2-key-1",
                  reason="Avoid the known pre-trip closure",
                  target_vehicle_ids=[f"shuttle-{index:02d}" for index in range(1, 6)])
    values.update(changes)
    return RouteApprovalRequest(**values)


def apply_charger_command(store: TrainingStore, session_id: str) -> None:
    snapshot = store.snapshot(session_id)
    charger = snapshot.chargers[0]
    command = DispatcherCommand(contract_version="2.0", command_id="charger-race",
                                idempotency_key="charger-race-key", session_id=session_id,
                                command_type="RESERVE_CHARGER", expected_epoch=snapshot.session_epoch,
                                expected_revision=charger.aggregate_revision, target_type="CHARGER",
                                target_id=charger.charger_id, reason="Reserve charger concurrently",
                                payload={"option_id": "charge-option-shuttle-01-charger-1", "option_revision": 1})
    assert store.accept("actor-native", command).status == "QUEUED"
    proposed = snapshot.model_copy(deep=True)
    proposed.chargers[0].aggregate_revision += 1
    assert store.apply(command.command_id, proposed).status == "APPLIED"


def test_detour_comparison_and_durable_approval(tmp_path: Path, service: RouteEditService) -> None:
    with TrainingStore(tmp_path / "training.sqlite") as store:
        before = store.create_session(initial())
        preview = service.preview(before, draft(via_node_ids=[26970886]))
        assert preview.validation == "PASS" and preview.approvable
        assert preview.directed_edge_ids == json.loads(ROUTE_FIXTURE.read_text(encoding="utf-8"))["detour_directed_edge_ids"]
        assert preview.original.distance_m < preview.edited.distance_m
        assert preview.edited.stop_coverage == {"stop-terminal": True}
        assert preview.edited.energy_kwh > preview.original.energy_kwh
        assert len(preview.polyline) == len(preview.node_ids)
        result = service.approve(store, "actor-a", before.session_id, approval(preview))
        assert result.status == "APPLIED"
        assert service.approve(store, "actor-a", before.session_id,
                               approval(preview, command_id="retry-command")).result_event_id == result.result_event_id
        with pytest.raises(TrainingStoreError) as conflict:
            service.approve(store, "actor-a", before.session_id,
                            approval(preview, command_id="retry-changed", reason="Changed reason"))
        assert conflict.value.code == "IDEMPOTENCY_CONFLICT"
        after = store.snapshot(before.session_id)
        assert after.routes[0].revision == 2
        assert after.routes[0].directed_edge_ids == preview.directed_edge_ids
        # The approved route is drawable along its new path
        assert len(after.routes[0].edge_paths_m) == len(preview.directed_edge_ids)
        assert after.routes[0].edge_paths_m[0][0].x_m == preview.polyline[0].x
        assert all(v.route_revision == 2 for v in after.vehicles)
        assert [e.event_type for e in store.replay(before.session_id, 0).events] == ["ROUTE_APPROVED", "COMMAND_APPLIED"]


def test_editor_context_uses_authoritative_graph_closures_and_assumptions(service: RouteEditService) -> None:
    snapshot = initial()
    context = service.editor_context(snapshot, "route-m2-detour")
    config = json.loads(ROUTE_FIXTURE.read_text(encoding="utf-8"))
    assert {node.node_id for node in context.nodes} == set(config["operating_node_ids"])
    assert [edge.edge_id for edge in context.edges if edge.closed] == config["closed_directed_edge_ids"]
    assert context.original_directed_edge_ids == snapshot.routes[0].directed_edge_ids
    assert any("kWh/km" in assumption for assumption in context.assumptions)
    assert any("Simulation estimates" in assumption for assumption in context.assumptions)
    assert service.preview(snapshot, draft(via_node_ids=[26970886])).approvable
    with pytest.raises(TrainingStoreError):
        service.editor_context(snapshot, "unknown-route")


def test_applied_retry_survives_preview_loss_and_restart(tmp_path: Path, service: RouteEditService) -> None:
    path = tmp_path / "retry.sqlite"
    with TrainingStore(path) as store:
        before = store.create_session(initial())
        preview = service.preview(before, draft())
        request = approval(preview)
        applied = service.approve(store, "actor-a", before.session_id, request)
        service._previews.clear()
        assert service.approve(store, "actor-a", before.session_id, request).result_event_id == applied.result_event_id
        with pytest.raises(TrainingStoreError) as conflict:
            service.approve(store, "actor-a", before.session_id, approval(preview, reason="Changed reason"))
        assert conflict.value.code == "IDEMPOTENCY_CONFLICT"
    with TrainingStore(path) as recovered:
        assert service.approve(recovered, "actor-a", before.session_id, request).result_event_id == applied.result_event_id


def test_identical_retry_before_original_acceptance_applies_once(
    tmp_path: Path, service: RouteEditService, monkeypatch: pytest.MonkeyPatch,
) -> None:
    with TrainingStore(tmp_path / "retry-in-flight.sqlite") as store:
        before = store.create_session(initial())
        request = approval(service.preview(before, draft()))
        entered, release = Event(), Event()
        original_accept = store.accept

        def delayed_accept(actor_id, command):
            if not entered.is_set():
                entered.set()
                assert release.wait(timeout=10)
            return original_accept(actor_id, command)

        monkeypatch.setattr(store, "accept", delayed_accept)
        with ThreadPoolExecutor(max_workers=1) as pool:
            original = pool.submit(service.approve, store, "actor-a", before.session_id, request)
            try:
                assert entered.wait(timeout=10)
                with pytest.raises(TrainingStoreError) as missing:
                    store.command_status(before.session_id, "actor-a", request.idempotency_key)
                assert missing.value.code == "INVALID_REFERENCE"
                replayed = service.approve(store, "actor-a", before.session_id, request)
            finally:
                release.set()
            applied = original.result(timeout=10)
        assert applied.status == replayed.status == "APPLIED"
        assert applied.command_id == replayed.command_id == request.command_id
        assert applied.result_event_id == replayed.result_event_id
        assert store.snapshot(before.session_id).routes[0].revision == 2
        assert [event.event_type for event in store.replay(before.session_id, 0).events].count("ROUTE_APPROVED") == 1


def test_queued_retry_finishes_after_failed_apply_and_preview_loss(
    tmp_path: Path, service: RouteEditService, monkeypatch: pytest.MonkeyPatch,
) -> None:
    with TrainingStore(tmp_path / "retry-queued.sqlite") as store:
        before = store.create_session(initial())
        preview = service.preview(before, draft())
        request = approval(preview)
        original_apply = store.apply
        attempts = 0

        def fail_once(*args, **kwargs):
            nonlocal attempts
            attempts += 1
            if attempts == 1:
                raise RuntimeError("injected failure after accept")
            return original_apply(*args, **kwargs)

        monkeypatch.setattr(store, "apply", fail_once)
        with pytest.raises(RuntimeError, match="injected failure"):
            service.approve(store, "actor-a", before.session_id, request)
        assert store.command_status(before.session_id, "actor-a", request.idempotency_key).status == "QUEUED"
        service._previews.clear()
        applied = service.approve(store, "actor-a", before.session_id, request)
        assert applied.status == "APPLIED"
        assert [event.event_type for event in store.replay(before.session_id, 0).events] == [
            "ROUTE_APPROVED", "COMMAND_APPLIED"]


def test_concurrent_charger_command_before_staging_is_included(
    tmp_path: Path, service: RouteEditService, monkeypatch: pytest.MonkeyPatch,
) -> None:
    with TrainingStore(tmp_path / "before-stage.sqlite") as store:
        before = store.create_session(initial())
        preview = service.preview(before, draft())
        original_accept = store.accept

        def accept_with_charger(actor_id, command):
            receipt = original_accept(actor_id, command)
            if command.command_type == "APPROVE_ROUTE":
                apply_charger_command(store, before.session_id)
            return receipt

        monkeypatch.setattr(store, "accept", accept_with_charger)
        result = service.approve(store, "actor-a", before.session_id, approval(preview))
        assert result.status == "APPLIED"
        assert store.snapshot(before.session_id).routes[0].revision == 2
        assert [event.event_type for event in store.replay(before.session_id, 0).events] == [
            "COMMAND_APPLIED", "ROUTE_APPROVED", "COMMAND_APPLIED"]


def test_concurrent_charger_command_after_staging_durably_rejects(
    tmp_path: Path, service: RouteEditService, monkeypatch: pytest.MonkeyPatch,
) -> None:
    with TrainingStore(tmp_path / "after-stage.sqlite") as store:
        before = store.create_session(initial())
        preview = service.preview(before, draft())
        request = approval(preview)
        original_apply = store.apply

        def apply_with_charger(command_id, *args, **kwargs):
            if command_id == request.command_id:
                apply_charger_command(store, before.session_id)
            return original_apply(command_id, *args, **kwargs)

        monkeypatch.setattr(store, "apply", apply_with_charger)
        result = service.approve(store, "actor-a", before.session_id, request)
        assert result.status == "REJECTED" and result.failure_code == "STALE_REVISION"
        assert store.command_status(before.session_id, "actor-a", request.idempotency_key).status == "REJECTED"
        assert store.snapshot(before.session_id).routes[0].revision == 1
        assert [event.event_type for event in store.replay(before.session_id, 0).events] == [
            "COMMAND_APPLIED", "COMMAND_REJECTED"]


@pytest.mark.parametrize("changes,failed_check", [
    ({"via_node_ids": [999999999]}, "GEOMETRY"),
    ({"directed_edge_ids": ["428288639:0:reverse"]}, "DIRECTION"),
    ({"directed_edge_ids": ["1535209095:1:forward"]}, "CLOSURE"),
    ({"directed_edge_ids": ["1535209095:0:reverse"]}, "GEOMETRY"),
])
def test_invalid_routes_have_actionable_checks(service: RouteEditService, changes, failed_check) -> None:
    preview = service.preview(initial(), draft(**changes))
    assert not preview.approvable and preview.validation == "FAIL"
    assert any(check.check == failed_check and check.status == "FAIL" for check in preview.checks)


def test_missing_stop_and_unknown_mandatory_metadata(service: RouteEditService) -> None:
    snapshot = initial()
    snapshot.routes[0].required_stop_ids = ["stop-unmapped"]
    missed = service.preview(snapshot, draft())
    assert any(check.check == "STOPS" and check.status == "FAIL" for check in missed.checks)
    original = service._fixture
    service._fixture = {**original, "detour_directed_edge_ids": original["detour_directed_edge_ids"][:-1]}
    try:
        unknown = service.preview(initial(), draft(directed_edge_ids=original["detour_directed_edge_ids"]))
        assert any(check.status == "UNKNOWN" and check.mandatory for check in unknown.checks)
        assert not unknown.approvable
    finally:
        service._fixture = original
    service._fixture = {key: value for key, value in original.items() if key != "road_width_m"}
    try:
        unknown_fit = service.preview(initial(), draft())
        assert any(check.check == "VEHICLE_FIT" and check.status == "UNKNOWN" for check in unknown_fit.checks)
        assert not unknown_fit.approvable
    finally:
        service._fixture = original


def test_stale_closure_and_conflicting_approvals(tmp_path: Path, service: RouteEditService) -> None:
    with TrainingStore(tmp_path / "training.sqlite") as store:
        before = store.create_session(initial())
        first = service.preview(before, draft())
        second = service.preview(before, draft(draft_revision=4))
        assert service.approve(store, "actor-a", before.session_id, approval(first)).status == "APPLIED"
        with pytest.raises(TrainingStoreError) as stale:
            service.approve(store, "actor-b", before.session_id,
                            approval(second, command_id="m2-command-2", idempotency_key="m2-key-2"))
        assert stale.value.code == "STALE_REVISION"
    changed = initial()
    changed.disruptions[0].aggregate_revision += 1
    with TrainingStore(tmp_path / "changed.sqlite") as store:
        store.create_session(changed)
        with pytest.raises(TrainingStoreError) as stale:
            service.approve(store, "actor-a", changed.session_id,
                            approval(first, command_id="m2-command-3", idempotency_key="m2-key-3"))
        assert stale.value.code == "STALE_REVISION"


def test_simultaneous_accepted_approvals_have_one_durable_winner(
    tmp_path: Path, service: RouteEditService, monkeypatch: pytest.MonkeyPatch,
) -> None:
    with TrainingStore(tmp_path / "race.sqlite") as store:
        before = store.create_session(initial())
        previews = [service.preview(before, draft(draft_revision=index)) for index in (3, 4)]
        rendezvous = Barrier(2)
        original_accept = store.accept

        def accept_together(actor_id, command):
            receipt = original_accept(actor_id, command)
            rendezvous.wait(timeout=10)
            return receipt

        monkeypatch.setattr(store, "accept", accept_together)
        with ThreadPoolExecutor(max_workers=2) as pool:
            futures = [pool.submit(service.approve, store, f"actor-{index}", before.session_id,
                                   approval(preview, command_id=f"race-{index}",
                                            idempotency_key=f"race-key-{index}"))
                       for index, preview in enumerate(previews)]
            outcomes = [future.result(timeout=15) for future in futures]
        assert sorted(outcome.status for outcome in outcomes) == ["APPLIED", "REJECTED"]
        assert store.snapshot(before.session_id).routes[0].revision == 2
        assert [event.event_type for event in store.replay(before.session_id, 0).events].count("ROUTE_APPROVED") == 1


def test_route_http_contract(tmp_path: Path, service: RouteEditService) -> None:
    with TrainingStore(tmp_path / "training.sqlite") as store:
        store.create_session(initial())
        app.dependency_overrides[get_training_store] = lambda: store
        app.dependency_overrides[get_actor_id] = lambda: "actor-a"
        app.dependency_overrides[get_route_edit_service] = lambda: service
        try:
            with TestClient(app) as client:
                response = client.get("/api/v2/sessions/session-001/routes/route-m2-detour/editor")
                assert response.status_code == 200
                assert len(response.json()["nodes"]) == 7
                assert any(edge["closed"] for edge in response.json()["edges"])
                response = client.post("/api/v2/sessions/session-001/route-previews", json=draft().model_dump())
                assert response.status_code == 200
                body = response.json()
                assert body["approvable"] and body["preview_revision"] == 3
                response = client.post("/api/v2/sessions/session-001/route-approvals",
                                       json=approval(RouteEditPreview.model_validate(body)).model_dump())
                assert response.status_code == 200 and response.json()["status"] == "APPLIED"
        finally:
            app.dependency_overrides.clear()


def test_routes_that_serve_the_same_stops_are_refused():
    """A detour is traced back to the route it was taken from by its stops: two routes with the same stops
    would make that ambiguous, so the editor refuses them."""
    config = json.loads(ROUTE_FIXTURE.read_text(encoding="utf-8"))
    config["peer_routes"] = {"route-twin": {key: config[key] for key in
                                            ("start_node_id", "destination_node_id", "required_stops")}}
    with pytest.raises(ValueError, match="serve the same stops"):
        RouteEditService(get_road_graph_repository(), fixture=config)
