"""BE-M2-09 reproducible prerequisites, adapter samples and honest gate report.

This runner deliberately does not certify rendered clients or P1 performance.
It identifies concrete workflow/content gaps against the existing 3D actor.
No files, asset decoding, or report construction run inside the actor's tick.
"""

from __future__ import annotations

import argparse
from datetime import UTC, datetime
import hashlib
import json
import math
from pathlib import Path
import platform
import subprocess
from time import perf_counter_ns

from contracts.v2.models import parse_message
from contracts.v2.spatial import BodyPose, render_asset_point
from .glb_validator import decode_json, validate_glb
from .spatial_motion import RoadPath, WorkBudget, body_bounds
from .spatial_session_actor import SpatialSessionActor
from .training_scene import (PACKAGE_ROOT, TrainingScenePackage, canonical_json,
                             validate_snapshot_bindings)

ROOT = Path(__file__).resolve().parents[3]
FIXTURE_PATH = ROOT / "backend/fixtures/m2_3d_acceptance.json"
MOTION_PATH = ROOT / "contracts/v2/fixtures/m2_3d_motion.json"


def load_fixture(package: TrainingScenePackage) -> dict:
    fixture = json.loads(FIXTURE_PATH.read_bytes())
    reference = package.reference().model_dump()
    if any(reference[key] != value for key, value in fixture["spatial"].items()):
        raise ValueError("ACCEPTANCE_SCENE_PIN_MISMATCH")
    entities = {e.entity_id: e for e in package.manifest.entities}
    for identifiers, kind in ((fixture["vehicle_ids"], "SHUTTLE"),
                              (fixture["charger_entity_ids"], "CHARGER"),
                              ([fixture["obstruction_entity_id"]], "OBSTRUCTION"),
                              ([fixture["depot_entity_id"]], "DEPOT"),
                              ([fixture["stop_entity_id"]], "STOP")):
        if any(identifier not in entities or entities[identifier].kind != kind for identifier in identifiers):
            raise ValueError("ACCEPTANCE_ENTITY_BINDING_MISMATCH")
    return fixture


def _sample(actor: SpatialSessionActor) -> dict:
    snapshot = actor.generate_snapshot()
    validate_snapshot_bindings(snapshot, actor.package)
    # Independent asymmetric nose/roof markers catch asset/world basis errors.
    markers = {}
    for vehicle in snapshot.vehicles:
        p = vehicle.spatial
        pose = BodyPose(position_m=p.position_m, orientation_xyzw=p.orientation_xyzw)
        vertex = [2.1, .8, -.6]
        markers[vehicle.vehicle_id] = {
            "asset_vertex_m": vertex,
            "render_marker_m": render_asset_point(pose, p.asset_to_body, vertex),
        }
    return {"snapshot": snapshot.model_dump(mode="json"), "markers": markers}


def motion_fixture(package: TrainingScenePackage | None = None) -> dict:
    package = package or TrainingScenePackage(PACKAGE_ROOT)
    fixture = load_fixture(package)
    actors = [SpatialSessionActor(seed=fixture["seed"], package=package,
                                  session_id="m2-3d-acceptance", surface_ids=tuple(fixture["road_surface_ids"]),
                                  vehicle_ids=tuple(fixture["vehicle_ids"])) for _ in range(2)]
    samples = []
    for tick in range(fixture["steps"] + 1):
        if actors[0]._state_hash() != actors[1]._state_hash():
            raise AssertionError(f"REPLAY_DIVERGENCE_AT_{tick}")
        if tick in fixture["sample_ticks"]:
            samples.append(_sample(actors[0]))
        if tick != fixture["steps"]:
            for actor in actors:
                actor.tick()
    return {"fixture_id": fixture["fixture_id"], "evidence": "HEADLESS_ADAPTER_SAMPLES",
            "connected_clients": [], "interpolation": "DISABLED",
            "spatial": package.reference().model_dump(), "seed": fixture["seed"],
            "verified_steps": fixture["steps"], "replay_matches": True,
            "tolerances": fixture["tolerances"], "samples": samples}


def workflow_audit(snapshot, package: TrainingScenePackage, fixture: dict) -> list[dict]:
    """Check real references; a nearby mesh or matching label is insufficient."""
    route = json.loads((ROOT / "backend/fixtures/m2_route_detour.json").read_bytes())
    incident = json.loads((ROOT / "backend/fixtures/m2_incident.json").read_bytes())
    edges = {s.directed_edge_id for s in package.manifest.road_surfaces}
    missing_edges = sorted(set(route["original_directed_edge_ids"] + route["detour_directed_edge_ids"]) - edges)
    entities = {e.entity_id: e for e in package.manifest.entities}
    active_chargers = {c.charger_id for c in snapshot.chargers}
    checks = []

    def check(identifier, passed, owner, detail):
        checks.append({"check": identifier, "status": "PASS" if passed else "BLOCKED",
                       "owner": owner, "detail": detail})

    check("five_spatial_bodies", [v.vehicle_id for v in snapshot.vehicles] == fixture["vehicle_ids"],
          "BE-M2-09", {"vehicle_ids": [v.vehicle_id for v in snapshot.vehicles]})
    check("three_service_patterns", len(snapshot.routes) == 3, "BE-M2-09",
          {"route_ids": [r.route_id for r in snapshot.routes], "required_count": 3})
    check("route_edit_surface_binding", not missing_edges, "BE-M2-09 + content",
          {"fixture_route_id": route["route_id"], "missing_surface_edge_ids": missing_edges})
    check("incident_actor_binding", snapshot.scenario.scenario_id == incident["scenario_id"]
          and any(r.route_id == incident["route_id"] for r in snapshot.routes)
          and incident.get("obstruction_entity_id") == fixture["obstruction_entity_id"],
          "BE-M2-09",
          {"actor_scenario_id": snapshot.scenario.scenario_id,
           "incident_scenario_id": incident["scenario_id"], "incident_route_id": incident["route_id"],
           "incident_disruption_id": incident["disruption_id"],
           "incident_obstruction_entity_id": incident.get("obstruction_entity_id"),
           "visible_obstruction_entity_id": fixture["obstruction_entity_id"]})
    # A depot mesh alone does not admit driving. Every proposed bay must attach
    # to an authored path reachable through directed topology from this route.
    surfaces = {s.surface_id: s for s in package.manifest.road_surfaces}
    reachable = set(fixture["road_surface_ids"])
    pending = list(reachable)
    while pending:
        for key in surfaces[pending.pop()].permitted_next_surface_ids:
            if key not in reachable:
                reachable.add(key)
                pending.append(key)
    bay_supports = []
    for bay in fixture["proposed_bays"]:
        matches = []
        for key in sorted(reachable):
            try:
                RoadPath([surfaces[key]]).project(bay["support_position_m"])
                matches.append(key)
            except ValueError:
                pass
        bay_supports.append({**bay, "reachable_surface_ids": matches})
    check("depot_and_bay_support", all(b["reachable_surface_ids"] for b in bay_supports), "BE-M2-09 + content",
          {"depot_position_m": entities[fixture["depot_entity_id"]].position_m,
           "proposed_bays": bay_supports,
           "reason": "Authored depot access and bay support/topology are required before admission."})
    check("charger_runtime_binding", set(fixture["charger_entity_ids"]) <= active_chargers, "BE-M2-09",
          {"scene_charger_ids": fixture["charger_entity_ids"], "runtime_charger_ids": sorted(active_chargers),
           "scene_charger_positions_m": {key: entities[key].position_m for key in fixture["charger_entity_ids"]}})
    return checks


def compare_observation(snapshot, observation: dict, tolerances: dict) -> dict:
    """Compare a client's canonical export against one exact committed sample.

    A passing comparison is numeric evidence only. It never implies that the
    export was obtained from a visible renderer or the same live connection.
    """
    for key in ("session_id", "session_epoch", "tick", "snapshot_id", "scene_revision"):
        if observation.get(key) != getattr(snapshot, key):
            raise ValueError(f"OBSERVATION_BOUNDARY_MISMATCH:{key}")
    if observation.get("spatial") != snapshot.spatial.model_dump():
        raise ValueError("OBSERVATION_SPATIAL_PIN_MISMATCH")
    if observation.get("interpolation") != "DISABLED":
        raise ValueError("OBSERVATION_INTERPOLATION_MUST_BE_DISABLED")
    if observation.get("client") not in {"web", "native"}:
        raise ValueError("OBSERVATION_CLIENT_REQUIRED")
    records = observation.get("vehicles")
    if not isinstance(records, list) or len(records) != len(snapshot.vehicles):
        raise ValueError("OBSERVATION_ROSTER_MISMATCH")
    if any(not isinstance(v, dict) or not isinstance(v.get("vehicle_id"), str) for v in records):
        raise ValueError("OBSERVATION_ROSTER_MISMATCH")
    by_id = {v["vehicle_id"]: v for v in records}
    if set(by_id) != {v.vehicle_id for v in snapshot.vehicles}:
        raise ValueError("OBSERVATION_ROSTER_MISMATCH")
    if observation.get("chargers") != [c.model_dump(mode="json") for c in snapshot.chargers]:
        raise ValueError("OBSERVATION_CHARGER_STATE_MISMATCH")
    maximum = {"position_m": 0., "orientation_deg": 0., "body_extent_m": 0., "asset_marker_m": 0.}

    def vector(record, key, count):
        value = record.get(key)
        if (not isinstance(value, list) or len(value) != count
                or any(type(x) not in (float, int) or not math.isfinite(x) for x in value)):
            raise ValueError(f"OBSERVATION_INVALID_VECTOR:{key}")
        return value

    for vehicle in snapshot.vehicles:
        record, pose = by_id[vehicle.vehicle_id], vehicle.spatial
        for key in ("support_surface_id", "lane_id", "deck_id", "collision_proxy_id",
                    "collision_proxy_revision", "render_asset_id", "render_asset_sha256"):
            if record.get(key) != getattr(pose, key):
                raise ValueError(f"OBSERVATION_BINDING_MISMATCH:{key}")
        if record.get("route_id") != vehicle.route_id or record.get("route_revision") != vehicle.route_revision:
            raise ValueError("OBSERVATION_ROUTE_MISMATCH")
        if record.get("service_state") != vehicle.service_state:
            raise ValueError("OBSERVATION_SERVICE_STATE_MISMATCH")
        maximum["position_m"] = max(maximum["position_m"], math.dist(vector(record, "position_m", 3), pose.position_m))
        q = vector(record, "orientation_xyzw", 4)
        if abs(sum(x*x for x in q) - 1) > 1e-6:
            raise ValueError("OBSERVATION_NON_UNIT_ORIENTATION")
        # Normalize both within their schema tolerance before measuring angle.
        dot = abs(sum(a*b for a, b in zip(q, pose.orientation_xyzw)))
        dot /= math.sqrt(sum(x*x for x in q)*sum(x*x for x in pose.orientation_xyzw))
        maximum["orientation_deg"] = max(maximum["orientation_deg"], math.degrees(2*math.acos(min(1., dot))))
        dims = vector(record, "dimensions_m", 3)
        offset = record.get("support_offset_m")
        if type(offset) not in (int, float) or not math.isfinite(offset) or offset < 0 or any(x <= 0 for x in dims):
            raise ValueError("OBSERVATION_INVALID_DIMENSIONS_OR_SUPPORT")
        maximum["body_extent_m"] = max(maximum["body_extent_m"], abs(offset-pose.support_offset_m),
                                        *(abs(a-b)/2 for a, b in zip(dims, pose.dimensions_m)))
        expected = render_asset_point(BodyPose(position_m=pose.position_m, orientation_xyzw=pose.orientation_xyzw),
                                      pose.asset_to_body, [2.1, .8, -.6])
        maximum["asset_marker_m"] = max(maximum["asset_marker_m"], math.dist(vector(record, "render_marker_m", 3), expected))
    limits = {**tolerances, "asset_marker_m": tolerances["position_m"]}
    return {"client": observation["client"], "tick": snapshot.tick,
            "status": "PASS" if all(value <= limits[key] for key, value in maximum.items()) else "FAIL",
            "evidence": "NUMERIC_OBSERVATION_ONLY", "maximum_errors": maximum}


def timing_summary(samples: list[float]) -> dict:
    ordered = sorted(samples)
    if not ordered or any(not math.isfinite(x) or x < 0 for x in ordered):
        raise ValueError("INVALID_TIMING_SAMPLES")
    return {"samples": len(ordered), "p95_ms": ordered[math.ceil(.95*len(ordered))-1],
            "p99_ms": ordered[math.ceil(.99*len(ordered))-1], "max_ms": ordered[-1],
            "small_sample": len(ordered) < 1000}


def compare_bundle(path: Path, package: TrainingScenePackage, tolerances: dict) -> list[dict]:
    with path.open("rb") as source:
        data = source.read(1024*1024 + 1)
    if len(data) > 1024*1024:
        raise ValueError("OBSERVATION_BYTE_LIMIT")
    bundle = decode_json(data)
    snapshot = parse_message(bundle["snapshot"])
    validate_snapshot_bindings(snapshot, package)
    observations = bundle["observations"]
    if (not isinstance(observations, list) or len(observations) != 2
            or any(not isinstance(o, dict) for o in observations)
            or {o.get("client") for o in observations} != {"web", "native"}):
        raise ValueError("OBSERVATION_BOTH_CLIENTS_REQUIRED")
    return [compare_observation(snapshot, o, tolerances) for o in observations]


def diagnostics(package: TrainingScenePackage, fixture: dict) -> dict:
    """Raw CPU spans, including every measured tick. No wall schedule/transport."""
    actor = SpatialSessionActor(seed=fixture["seed"], package=package, session_id="m2-3d-diagnostic")
    raw = {"tick_compute_ms": [], "snapshot_build_encode_ms": [], "static_query_ms": [], "asset_validate_ms": []}
    work, payload_sizes = [], []
    for _ in range(fixture["steps"]):
        start = perf_counter_ns()
        actor.tick()
        raw["tick_compute_ms"].append((perf_counter_ns()-start)/1e6)
        work.append(actor.spatial_work)
        if actor.should_emit_snapshot():
            start = perf_counter_ns()
            snapshot = actor.generate_snapshot()
            payload = snapshot.model_dump_json().encode()
            raw["snapshot_build_encode_ms"].append((perf_counter_ns()-start)/1e6)
            payload_sizes.append(len(payload))
            vehicle = snapshot.vehicles[0].spatial
            box = body_bounds(vehicle.position_m, vehicle.orientation_xyzw, vehicle.dimensions_m)
            start = perf_counter_ns()
            actor.static_index.query(box, WorkBudget(actor.limits.max_work_per_tick))
            raw["static_query_ms"].append((perf_counter_ns()-start)/1e6)
    assets = []
    for asset in package.manifest.assets:
        data = package.asset(asset.asset_id).data
        start = perf_counter_ns()
        inventory = validate_glb(data)
        raw["asset_validate_ms"].append((perf_counter_ns()-start)/1e6)
        assets.append({"asset_id": asset.asset_id, "sha256": asset.sha256, "encoded_bytes": len(data),
                       "decoded_bytes": inventory.decoded_bytes, "vertices": inventory.vertices,
                       "indices": inventory.indices, "triangles": inventory.indices//3})
    return {"profile": "P0_HEADLESS_DIAGNOSTIC", "clock": "perf_counter_ns", "warmup_steps": 0,
            "includes": "CPU actor, snapshot/JSON, committed-pose static queries, CPU GLB validation of verified resident bytes",
            "excludes": "durable I/O, scheduler lag, HTTP, client decode/upload, rendered frames, GPU, expanded P1",
            "summary": {key: timing_summary(value) for key, value in raw.items()}, "raw_ms": raw,
            "raw_sample_order": {"tick_compute_ms": "ticks 1..600", "snapshot_build_encode_ms": "ticks 2,4,..600",
                                 "static_query_ms": "ticks 2,4,..600", "asset_validate_ms": "asset_inventory order"},
            "spatial_work": {"maximum": max(work), "limit": actor.limits.max_work_per_tick},
            "telemetry_bytes": {"maximum": max(payload_sizes), "p1_limit": 65536}, "asset_inventory": assets}


def report() -> dict:
    start = perf_counter_ns()
    package = TrainingScenePackage(PACKAGE_ROOT)
    package_load_ms = (perf_counter_ns()-start)/1e6
    fixture = load_fixture(package)
    motion = motion_fixture(package)
    snapshot = parse_message(motion["samples"][0]["snapshot"])
    checks = workflow_audit(snapshot, package, fixture)
    # These require external client/runtime evidence, absent from this runner.
    # Do not infer a pass from contract parsers, client-reported readiness or
    # this small package's CPU timings.
    gates = [
        ("AT-13", "BLOCKED", "BE-M2-09 + WEB-M2-07..09 + NAT-M2-09..11", "Connected route/incident/charging walkthrough in two visible primary 3D worlds is absent."),
        ("AT-14", "NOT_RUN", "WEB-M2-08..09 + NAT-M2-10..11", "Adapter fixtures are available; connected same-tick rendered poses, extents and semantic picks are absent."),
        ("AT-15-M2", "PARTIAL", "BE-M2-09 + shared QA", "Headless ramp/obstruction/swept regressions exist; connected support/fit evidence is absent."),
        ("AT-17-M2", "PARTIAL", "WEB-M2-07 + NAT-M2-09 + shared QA", "Server readiness/asset rejection regressions exist; both client render-readiness failure paths are absent."),
        ("expanded-P1", "NOT_RUN", "BE-M2-06/09 + content + web/native", "Requires 5,000 directed edges, 200 static objects, three routes, two visible renderers and five 10-minute trials; current package is P0."),
    ]
    try:
        commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
        dirty = bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip())
    except (OSError, subprocess.CalledProcessError):
        commit, dirty = None, None
    return {"artifact_version": 1, "task": "BE-M2-09", "acceptance_status": "BLOCKED",
            "recorded_at_utc": datetime.now(UTC).isoformat(), "fixture_id": fixture["fixture_id"],
            "fixture_sha256": hashlib.sha256(canonical_json(fixture)).hexdigest(),
            "runner_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            "source_fixture_sha256": {name: hashlib.sha256((ROOT / "backend/fixtures" / name).read_bytes()).hexdigest()
                                      for name in ("m2_route_detour.json", "m2_incident.json", "routes_testing.json")},
            "source_commit": commit, "source_dirty": dirty,
            "host": {"os": platform.platform(), "python": platform.python_version(), "cpu": platform.processor(),
                     "gpu": None, "gpu_driver": None, "browser": None, "native_build": None,
                     "note": "Incomplete reference-machine inventory; no rendered P1 run was performed."},
            "spatial": package.reference().model_dump(), "connected_clients": [],
            "content_inventory": {
                "semantic_entities": len(package.manifest.entities),
                "static_instances_excluding_roads_and_shuttles": sum(e.kind not in {"ROAD", "SHUTTLE"} for e in package.manifest.entities),
                "buildings_and_obstructions": sum(e.kind in {"BUILDING", "OBSTRUCTION"} for e in package.manifest.entities),
                "stop_markers": sum(e.kind == "STOP" for e in package.manifest.entities),
                "directed_surface_edges": len(package.manifest.road_surfaces),
                "service_routes": len(snapshot.routes)},
            "headless_replay": {"status": "PASS", "steps": motion["verified_steps"],
                                "motion_fixture_sha256": hashlib.sha256(canonical_json(motion)).hexdigest()},
            "workflow_checks": checks,
            "acceptance_gates": [{"gate": gate, "status": status, "owner": owner, "reason": reason}
                                 for gate, status, owner, reason in gates],
            "package_load_ms": {"value": package_load_ms, "samples": 1, "filesystem_cache": "UNCONTROLLED"},
            "diagnostics": diagnostics(package, fixture)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--write-motion-fixture", action="store_true", help="Regenerate checked-in headless adapter samples")
    parser.add_argument("--report", type=Path, help="Write measured prerequisite report (never a rendered P1 result)")
    parser.add_argument("--observations", type=Path, help="Bounded JSON containing one backend snapshot and web/native canonical observations")
    parser.add_argument("--headless-only", action="store_true", help="Exit successfully after prerequisites; otherwise incomplete acceptance exits 2")
    args = parser.parse_args()
    if args.write_motion_fixture:
        MOTION_PATH.write_bytes(canonical_json(motion_fixture()))
    result = report()
    if args.observations:
        package = TrainingScenePackage(PACKAGE_ROOT)
        result["numeric_observations"] = compare_bundle(args.observations, package, load_fixture(package)["tolerances"])
    if args.report:
        args.report.write_bytes(canonical_json(result))
    print(json.dumps({"acceptance_status": result["acceptance_status"], "headless_replay": result["headless_replay"],
                      "workflow_checks": result["workflow_checks"], "acceptance_gates": result["acceptance_gates"],
                      "numeric_observations": result.get("numeric_observations", [])}, indent=2))
    if any(c["status"] != "PASS" for c in result.get("numeric_observations", [])):
        return 1
    return 0 if args.headless_only else 2


if __name__ == "__main__":
    raise SystemExit(main())
