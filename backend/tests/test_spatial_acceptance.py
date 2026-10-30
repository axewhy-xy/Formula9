"""BE-M2-09 same-boundary comparison and prevention of false acceptance."""

from copy import deepcopy
import json
import math

import pytest

from contracts.v2.models import parse_message
from contracts.v2.spatial import BodyPose, render_asset_point
from src.services import spatial_acceptance as acceptance
from src.services.training_scene import TrainingScenePackage, PACKAGE_ROOT, canonical_json


@pytest.fixture(scope="module")
def package():
    return TrainingScenePackage(PACKAGE_ROOT)


@pytest.fixture(scope="module")
def motion():
    return json.loads(acceptance.MOTION_PATH.read_bytes())


@pytest.fixture
def sample(motion):
    return parse_message(deepcopy(motion["samples"][2]["snapshot"]))


def observation(snapshot, client="web"):
    return {
        **{key: getattr(snapshot, key) for key in
           ("session_id", "session_epoch", "tick", "snapshot_id", "scene_revision")},
        "client": client, "interpolation": "DISABLED", "spatial": snapshot.spatial.model_dump(),
        "chargers": [c.model_dump(mode="json") for c in snapshot.chargers],
        "vehicles": [{**v.spatial.model_dump(), "vehicle_id": v.vehicle_id, "route_id": v.route_id,
                      "route_revision": v.route_revision, "service_state": v.service_state,
                      "render_marker_m": render_asset_point(
                          BodyPose(position_m=v.spatial.position_m, orientation_xyzw=v.spatial.orientation_xyzw),
                          v.spatial.asset_to_body, [2.1, .8, -.6])} for v in snapshot.vehicles],
    }


def test_fixture_is_reproducible_at_every_boundary(package, motion):
    generated = acceptance.motion_fixture(package)
    assert canonical_json(generated) == canonical_json(motion)
    assert generated["verified_steps"] == 600 and generated["replay_matches"]
    assert generated["connected_clients"] == []
    assert generated["evidence"] == "HEADLESS_ADAPTER_SAMPLES"
    assert [s["snapshot"]["tick"] for s in generated["samples"]] == [0, 40, 100, 240, 600]
    first, last = (parse_message(generated["samples"][i]["snapshot"]) for i in (0, -1))
    ramp_first, ramp_last = first.vehicles[-1].spatial, last.vehicles[-1].spatial
    assert ramp_last.position_m[2] > ramp_first.position_m[2]
    assert ramp_last.route_progress_m == pytest.approx(40 + math.hypot(20, 4))
    assert last.vehicles[-1].service_state == "STANDBY"
    assert last.vehicles[3].service_state == "BLOCKED"
    assert last.vehicles[3].spatial.position_m[0] + 2.4 < 29
    assert "obstruction-01" in generated["samples"][-1]["snapshot"]["spatial_diagnostics"]["shuttle-04"]["blockers"]


def test_scene_pin_change_fails_admission(package, monkeypatch, tmp_path):
    fixture = acceptance.load_fixture(package)
    fixture["spatial"]["manifest_sha256"] = "0" * 64
    path = tmp_path / "stale.json"
    path.write_bytes(canonical_json(fixture))
    monkeypatch.setattr(acceptance, "FIXTURE_PATH", path)
    with pytest.raises(ValueError, match="ACCEPTANCE_SCENE_PIN_MISMATCH"):
        acceptance.load_fixture(package)


def test_workflow_audit_preserves_real_integration_gaps(package, sample):
    checks = {c["check"]: c for c in acceptance.workflow_audit(sample, package, acceptance.load_fixture(package))}
    assert checks["five_spatial_bodies"]["status"] == "PASS"
    assert checks["three_service_patterns"]["detail"]["route_ids"] == ["route-m2-road-3d"]
    assert checks["route_edit_surface_binding"]["status"] == "BLOCKED"
    assert len(checks["route_edit_surface_binding"]["detail"]["missing_surface_edge_ids"]) == 7
    assert checks["incident_actor_binding"]["status"] == "BLOCKED"
    assert checks["incident_actor_binding"]["detail"]["incident_obstruction_entity_id"] is None
    assert checks["charger_runtime_binding"]["status"] == "BLOCKED"
    assert checks["depot_and_bay_support"]["status"] == "BLOCKED"
    assert all(b["reachable_surface_ids"] == [] for b in checks["depot_and_bay_support"]["detail"]["proposed_bays"])


def test_same_tick_observation_accepts_quaternion_sign_equivalence(sample, motion):
    export = observation(sample)
    for vehicle in export["vehicles"]:
        vehicle["orientation_xyzw"] = [-q for q in vehicle["orientation_xyzw"]]
    result = acceptance.compare_observation(sample, export, motion["tolerances"])
    assert result["status"] == "PASS"
    assert result["evidence"] == "NUMERIC_OBSERVATION_ONLY"


@pytest.mark.parametrize("key,value", [
    ("session_id", "peer-session"), ("session_epoch", 99), ("tick", 101),
    ("snapshot_id", "other-snapshot"), ("scene_revision", "other-scene"),
    ("interpolation", "ENABLED"), ("client", "protocol-harness"), ("chargers", [{}]),
])
def test_observation_rejects_other_boundary_or_hidden_interpolation(sample, motion, key, value):
    export = observation(sample)
    export[key] = value
    with pytest.raises(ValueError, match="OBSERVATION_"):
        acceptance.compare_observation(sample, export, motion["tolerances"])


@pytest.mark.parametrize("key", ["support_surface_id", "lane_id", "deck_id", "collision_proxy_id",
                                  "render_asset_sha256", "route_id", "route_revision", "service_state"])
def test_observation_rejects_semantic_disagreement(sample, motion, key):
    export = observation(sample)
    export["vehicles"][0][key] = "wrong"
    with pytest.raises(ValueError, match="OBSERVATION_"):
        acceptance.compare_observation(sample, export, motion["tolerances"])


@pytest.mark.parametrize("key,index,delta", [
    ("position_m", 0, .011), ("dimensions_m", 1, .041), ("render_marker_m", 0, .011),
])
def test_numeric_tolerance_miss_is_a_failure(sample, motion, key, index, delta):
    export = observation(sample)
    export["vehicles"][0][key][index] += delta
    assert acceptance.compare_observation(sample, export, motion["tolerances"])["status"] == "FAIL"


def test_orientation_and_support_tolerances_are_measured(sample, motion):
    export = observation(sample)
    export["vehicles"][0]["orientation_xyzw"] = [0, 0, math.sin(math.radians(.11)/2), math.cos(math.radians(.11)/2)]
    assert acceptance.compare_observation(sample, export, motion["tolerances"])["status"] == "FAIL"
    export = observation(sample)
    export["vehicles"][0]["support_offset_m"] += .021
    assert acceptance.compare_observation(sample, export, motion["tolerances"])["status"] == "FAIL"


@pytest.mark.parametrize("value", [[0, 0, float("nan")], [0, 0, float("inf")], [0, 0], [0, 0, True]])
def test_nonfinite_or_malformed_observation_cannot_pass(sample, motion, value):
    export = observation(sample)
    export["vehicles"][0]["position_m"] = value
    with pytest.raises(ValueError, match="INVALID_VECTOR"):
        acceptance.compare_observation(sample, export, motion["tolerances"])


@pytest.mark.parametrize("mutation", ["missing", "duplicate", "malformed"])
def test_observation_roster_must_be_exact(sample, motion, mutation):
    export = observation(sample)
    if mutation == "missing":
        export["vehicles"].pop()
    elif mutation == "duplicate":
        export["vehicles"][-1] = deepcopy(export["vehicles"][0])
    else:
        export["vehicles"][0] = {}
    with pytest.raises(ValueError, match="ROSTER_MISMATCH"):
        acceptance.compare_observation(sample, export, motion["tolerances"])


def test_percentiles_use_nearest_rank_and_preserve_outliers():
    summary = acceptance.timing_summary(list(range(100)) + [10000])
    assert summary == {"samples": 101, "p95_ms": 95, "p99_ms": 99, "max_ms": 10000, "small_sample": True}
    with pytest.raises(ValueError):
        acceptance.timing_summary([])


def test_paired_bundle_requires_two_exact_boundary_exports(package, sample, motion, tmp_path):
    path = tmp_path / "pair.json"
    bundle = {"snapshot": sample.model_dump(mode="json"),
              "observations": [observation(sample, "web"), observation(sample, "native")]}
    path.write_bytes(canonical_json(bundle))
    results = acceptance.compare_bundle(path, package, motion["tolerances"])
    assert [r["client"] for r in results] == ["web", "native"]
    assert all(r["status"] == "PASS" for r in results)
    bundle["observations"][1]["tick"] += 1
    path.write_bytes(canonical_json(bundle))
    with pytest.raises(ValueError, match="BOUNDARY_MISMATCH:tick"):
        acceptance.compare_bundle(path, package, motion["tolerances"])


@pytest.mark.parametrize("clients", [["web"], ["web", "web"], ["web", "native", "native"]])
def test_protocol_copies_cannot_substitute_for_a_client_pair(package, sample, motion, tmp_path, clients):
    path = tmp_path / "pair.json"
    path.write_bytes(canonical_json({"snapshot": sample.model_dump(mode="json"),
                                     "observations": [observation(sample, c) for c in clients]}))
    with pytest.raises(ValueError, match="BOTH_CLIENTS_REQUIRED"):
        acceptance.compare_bundle(path, package, motion["tolerances"])


def test_observation_input_has_a_byte_bound(package, motion, tmp_path):
    path = tmp_path / "large.json"
    path.write_bytes(b" " * (1024*1024 + 1))
    with pytest.raises(ValueError, match="OBSERVATION_BYTE_LIMIT"):
        acceptance.compare_bundle(path, package, motion["tolerances"])


def test_diagnostics_retain_all_raw_ticks_and_measured_inventory(package):
    result = acceptance.diagnostics(package, acceptance.load_fixture(package))
    assert result["profile"] == "P0_HEADLESS_DIAGNOSTIC"
    assert len(result["raw_ms"]["tick_compute_ms"]) == 600
    assert len(result["raw_ms"]["snapshot_build_encode_ms"]) == 300
    assert len(result["raw_ms"]["static_query_ms"]) == 300
    assert len(result["raw_ms"]["asset_validate_ms"]) == 9
    assert all(s["small_sample"] for s in result["summary"].values())
    assert result["telemetry_bytes"]["maximum"] <= 65536
    assert result["spatial_work"]["maximum"] <= result["spatial_work"]["limit"]
    assert sum(a["encoded_bytes"] for a in result["asset_inventory"]) == 16736


def test_report_never_substitutes_headless_results_for_p1(package, monkeypatch, motion):
    monkeypatch.setattr(acceptance, "motion_fixture", lambda _: motion)
    monkeypatch.setattr(acceptance, "diagnostics", lambda *_: {"profile": "P0_HEADLESS_DIAGNOSTIC"})
    result = acceptance.report()
    assert result["acceptance_status"] == "BLOCKED"
    assert result["connected_clients"] == []
    assert result["headless_replay"]["status"] == "PASS"
    gates = {g["gate"]: g["status"] for g in result["acceptance_gates"]}
    assert gates == {"AT-13": "BLOCKED", "AT-14": "NOT_RUN", "AT-15-M2": "PARTIAL",
                     "AT-17-M2": "PARTIAL", "expanded-P1": "NOT_RUN"}
    assert result["content_inventory"]["static_instances_excluding_roads_and_shuttles"] == 6


def test_cli_incomplete_acceptance_fails_unless_explicit_headless_scope(monkeypatch, capsys):
    monkeypatch.setattr(acceptance, "report", lambda: {"acceptance_status": "BLOCKED", "headless_replay": {},
                                                    "workflow_checks": [], "acceptance_gates": []})
    monkeypatch.setattr("sys.argv", ["spatial_acceptance"])
    assert acceptance.main() == 2
    monkeypatch.setattr("sys.argv", ["spatial_acceptance", "--headless-only"])
    assert acceptance.main() == 0
    assert '"acceptance_status": "BLOCKED"' in capsys.readouterr().out


def test_headless_cli_does_not_mask_failed_numeric_capture(monkeypatch, capsys):
    monkeypatch.setattr(acceptance, "report", lambda: {"acceptance_status": "BLOCKED", "headless_replay": {},
                                                    "workflow_checks": [], "acceptance_gates": []})
    monkeypatch.setattr(acceptance, "compare_bundle", lambda *_: [{"status": "FAIL"}])
    monkeypatch.setattr("sys.argv", ["spatial_acceptance", "--headless-only", "--observations", "capture.json"])
    assert acceptance.main() == 1
