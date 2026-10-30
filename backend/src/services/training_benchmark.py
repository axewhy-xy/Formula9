"""
Durable HTTP and actor instrumentation diagnostic for BE-M2-06.

Author: Nur Syazliyarni Binte Zazali
Date Created: 28/10/2026
Last Modified: 28/10/2026

Description:
    Exercises route approval, incident response, physical depot travel, charging
    and recovery on one integrated actor through an isolated SQLite store and
    in-process HTTP transport. The normal run uses the live wall-clock scheduler;
    an explicit manual clock supports fast correctness checks only.
    Writes raw timings, nearest-rank summaries, command traces, checks, source
    hashes, active real-time factor, committed-state age and open risks.
    Backend evidence does not establish client paint,
    real network reconnects or a client-rendered P1 result.
"""

from __future__ import annotations

import argparse
import asyncio
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor
from datetime import UTC, datetime
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import subprocess
from time import monotonic, sleep
from uuid import uuid4

from fastapi.testclient import TestClient

from contracts.v2.models import DispatcherCommand
from src.api.routes.training_v2 import get_actor_id, get_training_store
from src.app import app
from src.core.training_telemetry import TrainingTrace
from src.persistence.training_store import TrainingStore
from src.services.integrated_actor import SCENARIO_SELECTOR, get_workflow_definition
from src.services.training_sessions import TrainingSessionService

ROOT = Path(__file__).resolve().parents[3]
PROFILE = ROOT / "backend/fixtures/m2_instrumentation.json"


def summary(samples):
    """Return sample count and nearest-rank p95/p99/max, retaining outliers."""
    if not samples or any(not math.isfinite(x) or x < 0 for x in samples):
        raise ValueError("Invalid timing samples")
    values = sorted(samples)
    return {"samples": len(values), "p95_ms": values[math.ceil(.95*len(values))-1],
            "p99_ms": values[math.ceil(.99*len(values))-1], "max_ms": values[-1]}


def summarize(records):
    """Summarize durations separately by span, outcome, command and HTTP class."""
    # Keep errors, business outcomes, command classes and HTTP routes separate.
    groups = defaultdict(list)
    for record in records:
        key = (record["name"], record["outcome"], record.get("command_type"),
               record.get("route"), record.get("method"), record.get("error_code"),
               record.get("failure_code"), record.get("status_code"), record.get("running"))
        groups[key].append(record["duration_ms"])
    fields = ("name", "outcome", "command_type", "route", "method", "error_code", "failure_code", "status_code", "running")
    return [{**dict(zip(fields, key)), **summary(samples)} for key, samples in sorted(groups.items(), key=lambda x: str(x[0]))]


def diagnostic_budgets(records, targets):
    """Internal comparisons only; no server span substitutes for a client gate."""
    comparisons = []
    for name, field, target in (("tick.compute", "duration_ms", "tick_compute_ms"),
                                 ("tick.schedule", "lateness_ms", "tick_schedule_lateness_ms")):
        samples = [r for r in records if r["name"] == name and r["outcome"] == "OK"
                   and (name != "tick.schedule" or r.get("running"))]
        if not samples:
            continue
        measured = summary([r[field] for r in samples])
        missed = any(measured[key+"_ms"] > limit for key, limit in targets[target].items())
        comparisons.append({"metric": target, "status": "MISS" if missed else "WITHIN_DIAGNOSTIC_BUDGET",
                            "scope": "P0_INTERNAL_ONLY", "measured": measured, "target": targets[target],
                            "small_sample": len(samples) < 1000,
                            "slowest_records": [{key: r[key] for key in ("sequence", "session_id", "session_epoch", "tick", field)}
                                                for r in sorted(samples, key=lambda r: r[field], reverse=True)[:5]]})
        components = ("tick.stage", "tick.actor") if name == "tick.compute" else (
            "tick.sleep", "tick.lock_wait", "tick.worker_wait", "tick.boundary")
        comparisons[-1]["component_timings"] = {
            component: summary([r["duration_ms"] for r in records if r["name"] == component and r["outcome"] == "OK"
                                and (component != "tick.boundary" or r.get("running"))])
            for component in components if any(r["name"] == component and r["outcome"] == "OK" for r in records)}
    return comparisons


def runtime_metrics(records, *, manual_clock=False):
    """Summarize server monotonic progress, excluding pause/recovery intervals.

    Minute windows count a step when its installed result is observed, without
    interpolating progress across stalls. Partial windows are reported but do
    not establish the per-minute reference. Server age ends at observation;
    it does not include client transport, interpolation or paint.
    """
    progress = [r for r in records if r["name"] == "runtime.progress"]
    active = [r for r in progress if r["running"] or r["interval_running"]]
    age = {"scope": "SERVER_OBSERVED_ONLY", "observations": len(active),
        "origin": "BEGINNING_OF_PHYSICS_STEP",
        "peak_basis": "Conservative age just before commit replacement, observed after commit/install",
        "pause_policy": "Exclude paused-only observations; age on resume includes elapsed pause time",
        "unknown_current_observations": sum(r["committed_state_age_ms"] is None for r in active),
        "unknown_peak_observations": sum(r["oldest_committed_state_age_ms"] is None for r in active)}
    age["status"] = "NOT_MEASURED_MANUAL_CLOCK" if manual_clock else "MEASURED" if active else "NOT_OBSERVED"
    for field, label in (("committed_state_age_ms", "current"), ("oldest_committed_state_age_ms", "oldest")):
        values = [r[field] for r in active if r[field] is not None]
        age[label] = summary(values) if values and not manual_clock else None
    result = {"committed_state_age": age, "real_time_factor": {
        "scope": "BACKEND_ACTIVE_TIME_ONLY", "reference_minimum": .99,
        "status": "NOT_MEASURED_MANUAL_CLOCK" if manual_clock else "NOT_OBSERVED",
        "factor": None, "segments": [], "windows": [], "full_minute_windows": 0,
        "below_reference_full_minutes": 0}}
    if manual_clock:
        return result

    by_runtime = defaultdict(list)
    for r in progress:
        if not r["interval_running"]:
            continue
        start, end, advance = r["interval_start_ns"], r["end_ns"], r["simulation_advance_s"]
        if start is None or end < start or not math.isfinite(advance) or advance < 0 or (end == start and advance):
            raise ValueError("Invalid runtime progress interval")
        if end > start:
            by_runtime[(r["session_id"], r["session_epoch"])].append(r)
    segments = []
    for (session_id, epoch), intervals in sorted(by_runtime.items()):
        for r in sorted(intervals, key=lambda x: x["interval_start_ns"]):
            if (not segments or segments[-1]["session_id"] != session_id
                    or segments[-1]["session_epoch"] != epoch or segments[-1]["end_ns"] != r["interval_start_ns"]):
                segments.append({"session_id": session_id, "session_epoch": epoch,
                    "start_ns": r["interval_start_ns"], "end_ns": r["interval_start_ns"], "intervals": []})
            segments[-1]["intervals"].append(r)
            segments[-1]["end_ns"] = r["end_ns"]
    windows = []
    minute_ns = 60_000_000_000
    for segment in segments:
        intervals = segment.pop("intervals")
        duration = segment["end_ns"]-segment["start_ns"]
        segment["wall_time_s"] = duration/1e9
        segment["simulation_advance_s"] = math.fsum(r["simulation_advance_s"] for r in intervals)
        segment["factor"] = segment["simulation_advance_s"]/segment["wall_time_s"]
        buckets = [[] for _ in range((duration+minute_ns-1)//minute_ns)]
        for r in intervals:
            index = (r["end_ns"]-segment["start_ns"]-1)//minute_ns
            buckets[index].append(r["simulation_advance_s"])
        for index, advances in enumerate(buckets):
            advance = math.fsum(advances)
            wall_ns = min(minute_ns, duration-index*minute_ns)
            windows.append({"session_id": segment["session_id"], "session_epoch": segment["session_epoch"],
                "start_ns": segment["start_ns"]+index*minute_ns, "wall_time_s": wall_ns/1e9,
                "simulation_advance_s": advance, "factor": advance/(wall_ns/1e9),
                "full_minute": wall_ns == minute_ns})
    factor = result["real_time_factor"]
    full = [w for w in windows if w["full_minute"]]
    wall_time = math.fsum(s["wall_time_s"] for s in segments)
    simulation_time = math.fsum(s["simulation_advance_s"] for s in segments)
    factor.update(segments=segments, windows=windows, full_minute_windows=len(full),
        wall_time_s=wall_time, simulation_advance_s=simulation_time,
        factor=simulation_time/wall_time if wall_time else None,
        below_reference_full_minutes=sum(w["factor"] < .99 for w in full),
        status="BELOW_REFERENCE" if any(w["factor"] < .99 for w in full)
            else "WITHIN_REFERENCE" if full else "NO_FULL_MINUTE_WINDOWS" if segments else "NOT_OBSERVED")
    return result


def source_metadata():
    """Capture source/fixture hashes and available host details before collection."""
    def git(*args):
        try:
            return subprocess.check_output(["git", *args], cwd=ROOT, text=True).strip()
        except (OSError, subprocess.CalledProcessError):
            return None
    files = ["backend/fixtures/m2_instrumentation.json", "backend/fixtures/m2_integrated.json",
             "backend/fixtures/m2_incident.json", "backend/src/energy/model.py",
             "backend/src/energy/decisions.py", "backend/src/services/battery_decision.py",
             "backend/src/services/integrated_actor.py", "backend/src/services/training_workflow.py",
             "backend/src/services/route_edit.py", "backend/src/services/incidents.py",
             "backend/src/services/session_actor.py", "backend/tests/test_training_integration.py",
             "backend/src/services/scheduler_profile.py",
             "backend/src/services/crash_recovery.py", "backend/tests/test_crash_recovery.py",
             "backend/src/app.py", "backend/src/core/config.py", "backend/src/auth/middleware.py",
             "backend/src/auth/service.py", "backend/src/auth/store.py", "backend/src/auth/passwords.py",
             "backend/tests/test_training_telemetry.py",
             "backend/src/core/training_telemetry.py", "backend/src/persistence/training_store.py",
             "backend/src/services/training_sessions.py", "backend/src/services/training_benchmark.py",
             "backend/src/api/routes/training_v2.py", "backend/requirements.lock.txt"]
    status = git("status", "--porcelain")
    return {"source_commit": git("rev-parse", "HEAD"), "source_dirty": None if status is None else bool(status),
            "file_sha256": {name: hashlib.sha256((ROOT/name).read_bytes()).hexdigest() for name in files},
            "host": {"os": platform.platform(), "python": platform.python_version(),
                     "cpu": platform.processor(), "logical_cpu_count": os.cpu_count(),
                     "physical_cores": None, "ram_gib": None, "gpu": None, "vram_gib": None,
                     "ssd": None, "power_mode": None, "display_scaling": None, "driver": None,
                     "browser_build": None, "qt_build": None, "native_build": None,
                     "reference_hardware_locked": False}}


def command(snapshot, kind, *, target_type="SESSION", target_id=None, revision=None, payload=None):
    """Build a fixture command against a known session/aggregate revision."""
    return DispatcherCommand(contract_version="2.0", command_id=uuid4().hex, idempotency_key=uuid4().hex,
                             session_id=snapshot.session_id, command_type=kind,
                             expected_epoch=snapshot.session_epoch,
                             expected_revision=revision if revision is not None else snapshot.session.aggregate_revision,
                             target_type=target_type, target_id=target_id or snapshot.session_id,
                             payload=payload or {}, reason="Authored diagnostic decision")


class _ManualSessionService(TrainingSessionService):
    """Test-only clock; commands and steps still use the real lock/commit path."""
    async def _run(self, session):
        await asyncio.Event().wait()


def run(output: Path, *, ticks=None, validation_samples=None, reconnect_samples=None, manual_clock=False):
    """Collect the complete backend workflow in a new evidence directory.

    ticks is a minimum total logical runtime: even a short smoke run completes
    depot travel and charging. The manual clock accelerates correctness checks
    and cannot supply scheduler/performance evidence. Reconnect samples exercise
    baseline/event/receipt reconciliation through ASGI, not socket reconnects.
    """
    config = json.loads(PROFILE.read_bytes())
    ticks = config["ticks"] if ticks is None else ticks
    validation_samples = config["validation_samples"] if validation_samples is None else validation_samples
    reconnect_samples = config["reconnect_samples"] if reconnect_samples is None else reconnect_samples
    if (type(ticks) is not int or not 2 <= ticks <= 6000 or
            type(validation_samples) is not int or not 1 <= validation_samples <= 1000 or
            type(reconnect_samples) is not int or not 1 <= reconnect_samples <= 100 or
            type(manual_clock) is not bool or config["scenario_revision_id"] != SCENARIO_SELECTOR):
        raise ValueError("Diagnostic profile or sample counts are outside their declared bounds")
    output = Path(output)
    if output.exists() and any(output.iterdir()):
        raise ValueError("Output directory must be new or empty; existing evidence is never overwritten")
    output.mkdir(parents=True, exist_ok=True)
    trace = TrainingTrace(capacity=65536)
    checks, integration = [], []
    database = output / "runtime.sqlite"
    previous_overrides = dict(app.dependency_overrides)

    def check(name, passed, **evidence):
        checks.append({"check": name, "status": "PASS" if passed else "FAIL", "evidence": evidence})
        if not passed:
            raise AssertionError(name)

    def request(client, method, path, *, observer="protocol-a", expected=200, **kwargs):
        with trace.span("harness.http", method=method, route=path, observer=observer) as fields:
            response = client.request(method, path, **kwargs)
            fields.update(status_code=response.status_code, request_id=response.headers.get("x-training-trace-id"),
                          outcome="OK" if response.status_code < 400 else "HTTP_ERROR")
        if response.status_code != expected:
            raise AssertionError(f"{method} {path}: expected {expected}, received {response.status_code}")
        return response.json()

    def send(client, snapshot, value, *, expected=202):
        result = request(client, "POST", f"/api/v2/sessions/{snapshot.session_id}/commands",
                         json=value.model_dump(mode="json"), expected=expected)
        integration.append({"phase": "command", "submitted_command_id": value.command_id,
                            "command_type": value.command_type, "session_id": snapshot.session_id,
                            "expected_epoch": value.expected_epoch, "result": result})
        return result

    def mark(phase, snapshot, **evidence):
        integration.append({"phase": phase, "session_id": snapshot.session_id,
            "session_epoch": snapshot.session_epoch, "tick": snapshot.tick,
            "snapshot_id": snapshot.snapshot_id, "event_sequence": snapshot.event_sequence,
            "vehicles": [v.model_dump(mode="json") for v in snapshot.vehicles],
            "routes": [r.model_dump(mode="json") for r in snapshot.routes],
            "incidents": [i.model_dump(mode="json") for i in snapshot.incidents],
            "chargers": [c.model_dump(mode="json") for c in snapshot.chargers], **evidence})

    def bind(store):
        app.dependency_overrides[get_training_store] = lambda: store
        # Isolated fixture authentication; this never changes production auth.
        app.dependency_overrides[get_actor_id] = lambda: "benchmark-operator"
        if manual_clock:
            app.state.training_services = {store: _ManualSessionService(store)}

    def wait_for(client, store, session_id, predicate, phase, max_ticks=12000):
        start = store.snapshot(session_id).tick
        deadline = monotonic() + max(10, max_ticks * .15)

        def current():
            snapshot = store.snapshot(session_id)
            if snapshot.session.status != "RUNNING":
                raise AssertionError(f"Runtime stopped during {phase}: {snapshot.session.status}")
            if predicate(snapshot):
                mark(phase, snapshot)
                return snapshot
            if snapshot.tick - start >= max_ticks or monotonic() > deadline:
                raise TimeoutError(f"Integrated actor did not reach {phase}")
            return None

        if manual_clock:
            async def advance():
                service = app.state.training_services[store]
                while (snapshot := current()) is None:
                    session = service.sessions[session_id]
                    await service.step(session)
                    await service.step(session)
                return snapshot
            return client.portal.call(advance)
        while (snapshot := current()) is None:
            sleep(.05)
        # Read through the same public endpoint used by clients as well.
        request(client, "GET", f"/api/v2/sessions/{session_id}/snapshot")
        return snapshot

    def recover(client, store, before, pending=None):
        restored = request(client, "GET", f"/api/v2/sessions/{before.session_id}/snapshot")
        after = store.snapshot(before.session_id)
        check("restart_paused_new_epoch", restored["session"]["status"] == "PAUSED"
              and after.session_epoch == before.session_epoch + 1 and after.tick == before.tick)
        check("workflow_state_survives_restart", after.vehicles == before.vehicles
              and after.routes == before.routes and after.incidents == before.incidents
              and after.chargers == before.chargers)
        if pending is not None:
            receipt = request(client, "GET", f"/api/v2/sessions/{before.session_id}/commands/{pending.idempotency_key}")
            check("queued_requires_reconfirmation", receipt["status"] == "NEEDS_RECONFIRMATION")
        mark("recovered", after)
        return after

    def write(name, value):
        (output/name).write_text(json.dumps(value, indent=2, sort_keys=True)+"\n", encoding="utf-8")

    try:
        try:
            metadata = source_metadata()
            definition = get_workflow_definition()
            inventory = {"vehicles": 5, "service_patterns": len(definition.actor_routes),
                "chargers": len(definition.data["bay_positions_m"]),
                "route_validation_graph_edges": definition.graph.graph.number_of_edges(),
                "geometry": "AUTHORED_PLANAR", "fixture_signature": definition.signature}
            write("manifest.json", {**metadata, "profile": config, "content_inventory": inventory,
                "clock_mode": "MANUAL_TEST" if manual_clock else "WALL_CLOCK"})
            with TrainingStore(database, trace=trace) as store:
                bind(store)
                with TestClient(app) as client:
                    created = request(client, "POST", "/api/v2/sessions", expected=201,
                        headers={"Idempotency-Key": "benchmark-attempt"},
                        json={"scenario_revision_id": config["scenario_revision_id"], "seed": config["seed"]})
                    session_id = created["session_id"]
                    path = f"/api/v2/sessions/{session_id}"
                    initial = store.snapshot(session_id)
                    mark("created", initial)
                    check("one_integrated_runtime", store.runtime_ids() == [session_id]
                          and len(app.state.training_services[store].sessions) == 1
                          and len(initial.vehicles) == 5 and len(initial.routes) == 5 and len(initial.chargers) == 2)
                    blocked = send(client, initial, command(initial, "START"))
                    check("closed_route_prevents_start", blocked["status"] == "REJECTED"
                          and store.snapshot(session_id).session.status == "READY")

                    for index in range(validation_samples):
                        preview = request(client, "POST", path+"/route-previews", json={
                            "route_id": "route-m2-detour", "expected_route_revision": 1, "draft_revision": index+1})
                        check("curated_detour_validated", preview["approvable"] and preview["validation"] == "PASS")
                    approval = request(client, "POST", path+"/route-approvals", json={
                        "preview_id": preview["preview_id"], "preview_revision": preview["preview_revision"],
                        "command_id": uuid4().hex, "idempotency_key": "benchmark-route-approval",
                        "reason": "Avoid the authored permanent closure", "target_vehicle_ids": ["shuttle-01"]})
                    check("route_approval_durable", approval["status"] == "APPLIED"
                          and store.snapshot(session_id).vehicles[0].route_revision == 2)
                    integration.append({"phase": "command", "command_type": "APPROVE_ROUTE",
                        "session_id": session_id, "result": approval})
                    mark("route_approved", store.snapshot(session_id))

                    first = command(store.snapshot(session_id), "START")
                    applied = send(client, initial, first)
                    retried = send(client, initial, first.model_copy(update={"command_id": uuid4().hex}))
                    check("lost_acknowledgement_one_outcome", applied["status"] == "APPLIED"
                          and all(applied[key] == retried[key] for key in
                              ("command_id", "status", "applied_tick", "result_event_id", "target_revision")))
                    conflict = send(client, initial, first.model_copy(update={"reason": "Changed retry"}), expected=409)
                    check("changed_retry_conflicts", conflict["error"]["code"] == "IDEMPOTENCY_CONFLICT")

                    current = store.snapshot(session_id)
                    charge = next(o for o in current.charge_options if o.vehicle_id == "shuttle-05" and o.action == "CHARGE_NOW")
                    charger = next(c for c in current.chargers if c.charger_id == charge.charger_id)
                    battery = command(current, "RESERVE_CHARGER", target_type="CHARGER", target_id=charger.charger_id,
                        revision=charger.aggregate_revision, payload={"option_id": charge.option_id, "option_revision": charge.revision})
                    check("battery_reservation_durable", send(client, current, battery)["status"] == "APPLIED")
                    mark("charger_reserved", store.snapshot(session_id))

                    detected = wait_for(client, store, session_id, lambda s: bool(s.incidents), "incident_detected")
                    check("incident_stops_only_affected_vehicle", detected.vehicles[0].service_state == "AWAITING_GUIDANCE"
                          and detected.vehicles[0].position == initial.vehicles[0].position
                          and detected.vehicles[1].position != initial.vehicles[1].position)
                    check("depot_return_executes", detected.vehicles[4].service_state == "RETURNING_TO_DEPOT"
                          and detected.vehicles[4].position != initial.vehicles[4].position)
                    incident = detected.incidents[0]
                    history = request(client, "GET", path+f"/incidents/{incident.incident_id}/history")
                    check("ten_second_simulation_history", history["complete"] and history["camera"] is None
                          and history["source_mode"] == "HISTORICAL_SIMULATION")
                    current = store.snapshot(session_id)
                    incident = current.incidents[0]
                    option = next(c for c in current.candidates if c.action == "WAIT")
                    decision = command(current, "APPROVE_INCIDENT_OPTION", target_type="INCIDENT",
                        target_id=incident.incident_id, revision=incident.aggregate_revision,
                        payload={"candidate_id": option.candidate_id})
                    check("incident_decision_durable", send(client, current, decision)["status"] == "APPLIED")
                    current = store.snapshot(session_id)
                    check("pause_during_hold", send(client, current, command(current, "PAUSE"))["status"] == "APPLIED")
                    before = store.snapshot(session_id)
                    queued = command(before, "RESUME")
                    check("pending_input_durable", store.accept("benchmark-operator", queued).status == "QUEUED")
                    mark("hold_before_recovery", before)

            # Clean close/reopen verifies checkpoints, not forced process termination.
            with TrainingStore(database, trace=trace) as store:
                bind(store)
                with TestClient(app) as client:
                    current = recover(client, store, before, queued)
                    for prior in (first, battery, decision):
                        receipt = request(client, "GET", path+f"/commands/{prior.idempotency_key}")
                        check("applied_outcome_survives_restart", receipt["status"] == "APPLIED"
                              and receipt["command_id"] == prior.command_id)
                    check("resume_hold", send(client, current, command(current, "RESUME"))["status"] == "APPLIED")
                    wait_for(client, store, session_id,
                        lambda s: s.incidents[0].state == "RESOLVED", "incident_resolved", max_ticks=400)
                    start = definition.data["nodes"][str(definition.data["route_edit"]["start_node_id"])]
                    moved = wait_for(client, store, session_id, lambda s: (s.vehicles[0].position.x_m, s.vehicles[0].position.y_m) != tuple(start)
                                     and definition.lies_on(s.vehicles[0].position.x_m, s.vehicles[0].position.y_m, "bypass-b"),
                                     "approved_bypass_executing", max_ticks=120)
                    check("route_executes_after_hold", moved.vehicles[0].route_revision == 2)
                    charging = wait_for(client, store, session_id,
                        lambda s: s.vehicles[4].service_state == "CHARGING", "charging_started")
                    bay = definition.data["bay_positions_m"][charge.charger_id]
                    check("actual_bay_arrival", (charging.vehicles[4].position.x_m, charging.vehicles[4].position.y_m) == tuple(bay)
                          and charging.vehicles[4].position != initial.vehicles[4].position)
                    charge_start_kwh = charging.vehicles[4].battery_kwh
                    progressed = wait_for(client, store, session_id, lambda s: s.tick >= charging.tick+20,
                                          "charging_progress", max_ticks=40)
                    check("charging_adds_energy", progressed.vehicles[4].battery_kwh > charge_start_kwh)
                    check("pause_during_charge", send(client, progressed, command(progressed, "PAUSE"))["status"] == "APPLIED")
                    before = store.snapshot(session_id)
                    charge_clock = store.runtime(session_id)[1]["workflow"]["charge_elapsed"]

            with TrainingStore(database, trace=trace) as store:
                bind(store)
                with TestClient(app) as client:
                    current = recover(client, store, before)
                    check("charging_clock_survives_restart", store.runtime(session_id)[1]["workflow"]["charge_elapsed"] == charge_clock)
                    check("resume_charge", send(client, current, command(current, "RESUME"))["status"] == "APPLIED")
                    finished = wait_for(client, store, session_id,
                        lambda s: s.vehicles[4].service_state == "STANDBY", "charging_finished", max_ticks=1600)
                    check("charging_releases_port", finished.vehicles[4].battery_kwh > charge_start_kwh
                          and all("shuttle-05" not in c.occupied_vehicle_ids for c in finished.chargers))
                    if finished.tick < ticks:
                        wait_for(client, store, session_id, lambda s: s.tick >= ticks, "minimum_ticks",
                                 max_ticks=ticks-finished.tick+2)
                    for _ in range(config["lifecycle_pairs"]):
                        current = store.snapshot(session_id)
                        check("lifecycle_pause", send(client, current, command(current, "PAUSE"))["status"] == "APPLIED")
                        current = store.snapshot(session_id)
                        check("lifecycle_resume", send(client, current, command(current, "RESUME"))["status"] == "APPLIED")
                    current = store.snapshot(session_id)
                    check("final_pause", send(client, current, command(current, "PAUSE"))["status"] == "APPLIED")
                    with ThreadPoolExecutor(max_workers=2) as pool:
                        observations = list(pool.map(lambda label: request(client, "GET", path+"/snapshot", observer=label),
                                                     ("protocol-a", "protocol-b")))
                    check("concurrent_protocol_observers_same_boundary", observations[0] == observations[1])
                    final = store.snapshot(session_id)
                    mark("completed", final)
                    for _ in range(reconnect_samples):
                        with trace.span("harness.cached_baseline"):
                            baseline = request(client, "GET", path+"/snapshot", observer="protocol-b")
                            events = request(client, "GET", path+"/events", params={"after": 0}, observer="protocol-b")
                            receipt = request(client, "GET", path+f"/commands/{first.idempotency_key}", observer="protocol-b")
                            check("cached_baseline_reconciles", baseline["snapshot_id"] == final.snapshot_id
                                  and receipt["command_id"] == first.command_id and receipt["status"] == "APPLIED"
                                  and bool(events["events"]))
                    debrief = request(client, "GET", path+f"/incidents/{incident.incident_id}/debrief")
                    check("provisional_debrief_available", debrief["state"] == "RESOLVED"
                          and debrief["assessment"]["provisional"])
                    replay, cursor = [], 0
                    while True:
                        page = request(client, "GET", path+"/events", params={"after": cursor, "limit": 100})
                        replay.extend(page["events"])
                        if not page["has_more"]:
                            break
                        cursor = page["next_sequence"]
                    names = [e["event_type"] for e in replay]
                    check("one_ordered_workflow_event_log",
                        [e["event_sequence"] for e in replay] == list(range(1, final.event_sequence+1))
                        and {"ROUTE_APPROVED", "INCIDENT_DECISION_RECORDED", "INCIDENT_RESOLVED",
                             "CHARGER_RESERVED", "DEPOT_BAY_ARRIVED", "CHARGING_STARTED", "CHARGING_FINISHED"} <= set(names)
                        and names.count("CHARGER_RESERVED") == names.count("CHARGING_FINISHED") == 1)
                    integration.append({"phase": "event_replay", "session_id": session_id, "events": replay})
                    check("still_one_runtime", store.runtime_ids() == [session_id]
                          and len(app.state.training_services[store].sessions) == 1)
        finally:
            app.dependency_overrides.clear()
            app.dependency_overrides.update(previous_overrides)
            exported = trace.export()
            records = exported.pop("records")
            (output/"raw-timings.jsonl").write_text("".join(json.dumps(r, sort_keys=True)+"\n" for r in records), encoding="utf-8")
            write("trace-metadata.json", exported)
            write("integration-trace.json", integration)
            write("checks.json", checks)

        check("raw_trace_complete", exported["dropped_records"] == 0)
        schedule = [r for r in records if r["name"] == "tick.schedule" and r["running"]]
        if not manual_clock:
            check("live_scheduler_measured", len(schedule) > 0)
        budgets = diagnostic_budgets(records, config["targets"])
        metrics = runtime_metrics(records, manual_clock=manual_clock)
        check("runtime_progress_recorded", metrics["committed_state_age"]["observations"] > 0)
        if not manual_clock:
            check("runtime_metrics_measured", metrics["real_time_factor"]["factor"] is not None
                  and metrics["committed_state_age"]["oldest"] is not None)
        risks = [
            {"risk": "Client controls and command-to-paint probes are outside this backend run", "status": "NOT_RUN",
             "owner": "WEB-M2-03/04/05/06 + NAT-M2-04/05/06/07/08",
             "remediation_gate": "Run both visible clients on this session and correlate their displayed outcomes"},
            {"risk": "P1 content and reference hardware are not locked", "status": "BLOCKED",
             "owner": "BE-M2-06 + content + client owners",
             "remediation_gate": "Freeze the P1 content, hardware and quality inventory before shared acceptance"},
            {"risk": "In-process diagnostic does not establish network, paint or P1 percentiles", "status": "NOT_RUN",
             "owner": "BE-M2-06 + WEB-M2-06 + NAT-M2-08",
             "remediation_gate": "Collect five 10-minute paired production trials, 1000 samples/class and 50 real reconnects"},
            {"risk": "Clean-close recovery does not test abrupt process termination", "status": "NOT_RUN",
             "owner": "BE-M2-06", "remediation_gate": "Use a separate-process fault drill for crash acceptance"}]
        if manual_clock:
            risks.append({"risk": "Manual clock supplies correctness evidence only", "status": "NOT_RUN",
                          "owner": "BE-M2-06", "remediation_gate": "Run again without --manual-clock for scheduler timings"})
        if metrics["real_time_factor"]["below_reference_full_minutes"]:
            risks.append({"risk": "real_time_factor", "status": "MEASURED_DIAGNOSTIC_MISS", "owner": "BE-M2-06",
                "measured_evidence": metrics["real_time_factor"],
                "remediation_gate": "Profile active wall-clock boundaries and durable writes before the P1 gate"})
        risks.extend({"risk": item["metric"], "status": "MEASURED_DIAGNOSTIC_MISS", "owner": "BE-M2-06",
                      "measured_evidence": item,
                      "cause": "Stage exceeds its proposed budget; deeper cause requires profiling",
                      "remediation_gate": "Use the retained stage/outlier timings to profile the miss before P1"}
                     for item in budgets if item["status"] == "MISS")
        result = {"artifact_version": 3, "task": "BE-M2-06", "profile": config["profile"],
            "backend_integration_status": "PASS", "diagnostic_status": "PASS",
            "acceptance_status": "BLOCKED", "p1_gates": "NOT_RUN",
            "recorded_at_utc": datetime.now(UTC).isoformat(), **metadata,
            "parameters": {"minimum_ticks": ticks, "actual_ticks": final.tick,
                "validation_samples": validation_samples, "reconnect_samples": reconnect_samples,
                "warmup_seconds": 0, "production_builds": False, "transport": "IN_PROCESS_ASGI",
                "clock_mode": "MANUAL_TEST" if manual_clock else "WALL_CLOCK"},
            "session_id": session_id,
            "includes": "One integrated planar actor, durable HTTP commands, route execution, incident/history, physical depot travel, charging/release, two protocol observers and recovery during hold and charge",
            "excludes": "Visible web/native clients, network/loopback, command-to-paint, GPU, expanded P1 and forced process termination",
            "summary": summarize(records), "trace": exported, "checks": checks, **metrics,
            "tick_schedule": {"lateness": summary([r["lateness_ms"] for r in schedule]) if schedule else None,
                "running_boundaries": len(schedule),
                "all_boundaries": sum(r["name"] == "tick.schedule" for r in records),
                "missed_deadlines": sum(r["missed_deadlines"] for r in records if r["name"] == "tick.schedule")},
            "content_inventory": inventory, "targets": config["targets"], "open_risks": risks,
            "diagnostic_budget_observations": budgets, "integration_trace": integration}
        write("summary.json", result)
        write("checks.json", checks)
        write("open-risks.json", risks)
        return result
    except BaseException as error:
        write("failure.json", {"diagnostic_status": "FAIL", "acceptance_status": "BLOCKED",
            "error_code": getattr(error, "code", type(error).__name__), "checks": checks})
        raise


def main():
    """Run collection and return 0 for diagnostic success, 1 for failure, or 2 for incomplete acceptance."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--ticks", type=int)
    parser.add_argument("--validation-samples", type=int)
    parser.add_argument("--reconnect-samples", type=int)
    parser.add_argument("--manual-clock", action="store_true",
                        help="Accelerate correctness checks; do not collect wall-clock scheduler evidence")
    parser.add_argument("--diagnostic-only", action="store_true")
    args = parser.parse_args()
    result = run(args.output, ticks=args.ticks, validation_samples=args.validation_samples,
                 reconnect_samples=args.reconnect_samples, manual_clock=args.manual_clock)
    print(json.dumps({key: result[key] for key in ("profile", "diagnostic_status", "acceptance_status", "tick_schedule")}, indent=2))
    return 1 if result["diagnostic_status"] != "PASS" else 0 if args.diagnostic_only else 2


if __name__ == "__main__":
    raise SystemExit(main())
