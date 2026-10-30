"""
Regression tests for BE-M2-06 runtime instrumentation and diagnostic artifacts.

Author: Nur Syazliyarni Binte Zazali
Date Created: 28/10/2026
Last Modified: 28/10/2026

Description:
    Verifies bounded retention and overflow counts, request correlation across
    HTTP and SQLite writer work, durable application failures, queue saturation,
    actor trace isolation and missed-deadline recording. Exercises the diagnostic
    runner on one integrated actor, separate outcome summaries, recovery during
    hold/charging, pause/epoch-aware real-time factor, committed-state age,
    early/cancelled timer wakes and controlled scheduler profiling,
    failure artifacts and exit codes without claiming client paint.

"""

import asyncio
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import sqlite3
from threading import Event
from types import SimpleNamespace

from fastapi.testclient import TestClient
import pytest

from contracts.v2.models import FullSnapshot
from contracts.v2.sessions import CreateSessionRequest
from src.api.routes.training_v2 import get_actor_id, get_training_store
from src.app import app
from src.core.training_telemetry import SessionProgress, TrainingTrace, correlate
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.services.training_benchmark import command, diagnostic_budgets, main, run, runtime_metrics, summary, summarize
from src.services.training_sessions import TrainingSessionService, wait_for_deadline

ROOT = Path(__file__).resolve().parents[2]


def test_ring_retains_failures_and_explicitly_counts_overflow():
    ticks = iter(range(0, 100, 10))
    trace = TrainingTrace(capacity=2, clock=lambda: next(ticks))
    with correlate(request_id="request-a"):
        with trace.span("first"):
            pass
        with pytest.raises(RuntimeError):
            with trace.span("failed"):
                raise RuntimeError("do not export this secret")
    with trace.span("third"):
        pass
    exported = trace.export()
    assert exported["dropped_records"] == 1 and exported["total_records"] == 3
    failed, third = exported["records"]
    assert failed["request_id"] == "request-a" and failed["outcome"] == "ERROR"
    assert failed["error_code"] == "RuntimeError" and failed["duration_ms"] == .00001
    assert "request_id" not in third and "secret" not in json.dumps(exported)
    exported["records"][0]["name"] = "mutated"
    assert trace.export()["records"][0]["name"] == "failed"


def test_nearest_rank_separates_outcomes_and_never_discards_outliers():
    assert summary([1, 2, 3, 1000])["p99_ms"] == 1000
    records = [{"name": "command.accept", "outcome": outcome, "duration_ms": duration}
               for outcome, duration in (("QUEUED", 1), ("ERROR", 1000))]
    assert len(summarize(records)) == 2
    for samples in ([], [-1], [float("inf")], [float("nan")]):
        with pytest.raises(ValueError):
            summary(samples)


def test_progress_keeps_pose_age_across_commands_and_invalidates_it_on_recovery():
    clock = [0]
    trace = TrainingTrace(clock=lambda: clock[0])
    progress = SessionProgress()
    actor = SimpleNamespace(session_id="s", session_epoch=1, current_tick=0, is_paused=True, STEP_S=.05)
    def observe(seconds, tick, *, commit=None, sample=None, running=True):
        clock[0] = round(seconds*1e9)
        actor.current_tick = tick
        boundary = SimpleNamespace(session_epoch=actor.session_epoch, tick=commit, event_sequence=100+tick) if commit is not None else None
        progress.observe(trace, actor, committed=boundary,
                         sample_ns=round(sample*1e9) if sample is not None else None, running=running)
        return trace.export()["records"][-1]
    observe(0, 0, commit=0, running=False)
    observe(1, 0)
    assert observe(1.05, 1, sample=1)["committed_state_age_ms"] is None
    first = observe(1.12, 2, commit=2, sample=1.05)
    assert first["committed_state_age_ms"] == pytest.approx(70)
    # A command/retry at tick 2 does not make its existing vehicle pose fresh.
    assert observe(1.15, 2, commit=2)["committed_state_age_ms"] == pytest.approx(100)
    observe(1.2, 3, sample=1.15)
    replaced = observe(1.32, 4, commit=4, sample=1.2)
    assert replaced["committed_state_age_ms"] == pytest.approx(120)
    assert replaced["oldest_committed_state_age_ms"] == pytest.approx(270)
    stopped = observe(1.5, 4, running=False)
    assert stopped["interval_running"] and stopped["simulation_advance_s"] == 0
    actor.session_epoch = 2
    recovered = observe(10, 4, commit=4, running=False)
    assert recovered["committed_state_age_ms"] is None and not recovered["interval_running"]
    observe(11, 4)
    assert observe(11.05, 5, sample=11)["committed_state_age_ms"] is None
    assert observe(11.1, 6, commit=6, sample=11.05)["committed_state_age_ms"] == pytest.approx(50)


def progress_interval(start, end, advance, *, epoch=1, session_id="s"):
    return {"name": "runtime.progress", "session_id": session_id, "session_epoch": epoch,
        "running": True, "interval_running": True, "interval_start_ns": round(start*1e9),
        "end_ns": round(end*1e9), "simulation_advance_s": advance,
        "committed_state_age_ms": 10, "oldest_committed_state_age_ms": 50}


def test_realtime_factor_excludes_pause_and_recovery_downtime():
    records = [progress_interval(1, 2, 1), progress_interval(102, 103, 1), progress_interval(200, 201, 1, epoch=2)]
    measured = runtime_metrics(records)["real_time_factor"]
    assert measured["factor"] == 1 and measured["wall_time_s"] == 3
    assert len(measured["segments"]) == 3
    assert measured["full_minute_windows"] == 0 and measured["status"] == "NO_FULL_MINUTE_WINDOWS"


def test_realtime_windows_preserve_stalls_and_separate_partial_minutes():
    measured = runtime_metrics([progress_interval(0, 60, 60), progress_interval(60, 190, .05)])["real_time_factor"]
    assert [w["factor"] for w in measured["windows"]] == pytest.approx([1, 0, 0, .005])
    assert measured["full_minute_windows"] == 3 and measured["below_reference_full_minutes"] == 2
    assert measured["windows"][-1]["full_minute"] is False
    assert measured["factor"] == pytest.approx(60.05/190)
    assert measured["status"] == "BELOW_REFERENCE"


def test_realtime_factor_keeps_concurrent_sessions_separate():
    measured = runtime_metrics([progress_interval(0, 60, 60, session_id="a"),
                                progress_interval(0, 60, 60, session_id="b")])["real_time_factor"]
    assert measured["factor"] == 1 and len(measured["segments"]) == 2
    assert measured["full_minute_windows"] == 2 and measured["status"] == "WITHIN_REFERENCE"


def test_manual_clock_cannot_supply_realtime_or_age_acceptance_measurements():
    measured = runtime_metrics([progress_interval(0, 60, 60)], manual_clock=True)
    assert measured["real_time_factor"]["status"] == "NOT_MEASURED_MANUAL_CLOCK"
    assert measured["real_time_factor"]["factor"] is None and measured["real_time_factor"]["windows"] == []
    assert measured["committed_state_age"]["status"] == "NOT_MEASURED_MANUAL_CLOCK"
    assert measured["committed_state_age"]["oldest"] is None


@pytest.mark.parametrize("start,end,advance", [(2, 1, 1), (0, 0, 1), (0, 1, -1), (0, 1, float("nan"))])
def test_runtime_metrics_reject_invalid_monotonic_intervals(start, end, advance):
    with pytest.raises(ValueError, match="Invalid runtime progress"):
        runtime_metrics([progress_interval(start, end, advance)])


def test_writer_queue_saturation_is_correlated_and_bounded(tmp_path):
    started, release = Event(), Event()
    with TrainingStore(tmp_path/"queue.sqlite", queue_size=1) as store:
        def blocked(connection):
            started.set()
            assert release.wait(5)
        with ThreadPoolExecutor(max_workers=2) as pool:
            active = pool.submit(store._submit, blocked)
            assert started.wait(5)
            waiting = pool.submit(store._submit, lambda connection: None)
            # The writer is held; wait deterministically for the sole queued job.
            try:
                from time import monotonic, sleep
                deadline = monotonic()+5
                while store._jobs.empty():
                    assert monotonic() < deadline
                    sleep(.001)
                with correlate(command_id="overflow-command", session_id="session-a", session_epoch=3):
                    with pytest.raises(TrainingStoreError, match="queue is full"):
                        store._submit(lambda connection: None)
            finally:
                release.set()
            active.result()
            waiting.result()
        exported = store.trace.export()
        rejection = next(r for r in exported["records"] if r["name"] == "writer.queue_rejected")
        assert rejection["command_id"] == "overflow-command" and rejection["session_epoch"] == 3
        assert rejection["error_code"] == "QUEUE_FULL"
        assert exported["high_water"]["writer_queue_depth"] == 1


def test_http_commands_correlate_through_writer_without_polluting_actor_tasks(tmp_path):
    previous = dict(app.dependency_overrides)
    try:
        with TrainingStore(tmp_path/"http.sqlite") as store:
            app.dependency_overrides[get_training_store] = lambda: store
            app.dependency_overrides[get_actor_id] = lambda: "test-operator"
            with TestClient(app) as client:
                created = client.post("/api/v2/sessions", headers={"Idempotency-Key": "create"},
                                      json={"scenario_revision_id": "scenario-m2-r1", "seed": 1000})
                assert created.status_code == 201
                snapshot = store.snapshot(created.json()["session_id"])
                start = command(snapshot, "START")
                response = client.post(f"/api/v2/sessions/{snapshot.session_id}/commands", json=start.model_dump(mode="json"))
                assert response.status_code == 202 and response.json()["status"] == "APPLIED"
                request_id = response.headers["x-training-trace-id"]

                async def await_ticks():
                    async with asyncio.timeout(5):
                        while not any(r["name"] == "tick.compute" for r in store.trace.export()["records"]):
                            await asyncio.sleep(.01)
                client.portal.call(await_ticks)
                stale = command(store.snapshot(snapshot.session_id), "PAUSE", revision=999)
                denied = client.post(f"/api/v2/sessions/{snapshot.session_id}/commands", json=stale.model_dump(mode="json"))
                assert denied.status_code == 409
                records = store.trace.export()["records"]
                linked = [r for r in records if r.get("request_id") == request_id]
                assert {"writer.queue_wait", "writer.transaction", "command.accept", "command.apply", "http.publication"} <= {r["name"] for r in linked}
                accepted = next(r for r in linked if r["name"] == "command.accept")
                applied = next(r for r in linked if r["name"] == "command.apply")
                assert accepted["outcome"] == "QUEUED" and applied["outcome"] == "APPLIED"
                assert applied["command_id"] == start.command_id and applied["session_epoch"] == snapshot.session_epoch
                assert applied["command_type"] == "START" and applied["result_event_id"] == response.json()["result_event_id"]
                assert accepted["end_ns"] <= applied["end_ns"]
                assert all("request_id" not in r for r in records if r["name"].startswith("tick."))
                assert any(r["name"] == "command.accept" and r.get("error_code") == "STALE_REVISION" for r in records)
    finally:
        app.dependency_overrides.clear()
        app.dependency_overrides.update(previous)


def test_failed_application_reports_error_and_keeps_queued_durable_state(tmp_path, monkeypatch):
    raw = json.loads((ROOT/"contracts/v2/fixtures/snapshot.json").read_bytes())
    snapshot = FullSnapshot.model_validate(raw)
    snapshot.session.status = "READY"
    with TrainingStore(tmp_path/"failure.sqlite") as store:
        store.create_session(snapshot)
        value = command(snapshot, "START")
        store.accept("test", value)
        proposed = snapshot.model_copy(deep=True)
        proposed.session.status = "RUNNING"
        proposed.session.aggregate_revision += 1
        def fail(*args, **kwargs):
            raise sqlite3.OperationalError("private database error text")
        monkeypatch.setattr(store, "_write_snapshot", fail)
        with pytest.raises(sqlite3.OperationalError):
            store.apply(value.command_id, proposed)
        assert store.command_status(snapshot.session_id, "test", value.idempotency_key).status == "QUEUED"
        assert store.snapshot(snapshot.session_id).session.status == "READY"
        records = store.trace.export()["records"]
        apply = next(r for r in records if r["name"] == "command.apply")
        assert apply["outcome"] == "ERROR" and apply["error_code"] == "OperationalError"
        assert "private database" not in json.dumps(records)


def test_scheduler_counts_missed_deadlines_without_spawning_another_actor(tmp_path, monkeypatch):
    async def exercise():
        with TrainingStore(tmp_path/"late.sqlite") as store:
            service = TrainingSessionService(store)
            async def slow_boundary(session):
                await asyncio.sleep(.2)
            monkeypatch.setattr(service, "step", slow_boundary)
            try:
                await service.create(CreateSessionRequest(scenario_revision_id="scenario-m2-r1", seed=1000), "test", "create")
                async with asyncio.timeout(5):
                    while not any(r["name"] == "tick.schedule" for r in store.trace.export()["records"]):
                        await asyncio.sleep(.01)
                schedule = next(r for r in store.trace.export()["records"] if r["name"] == "tick.schedule")
                assert schedule["missed_deadlines"] >= 2 and schedule["boundary_ms"] >= 150
                assert len(service.sessions) == 1
            finally:
                await service.close()
    asyncio.run(exercise())


def test_deadline_wait_rechecks_early_wakes_before_allowing_a_tick(monkeypatch):
    now = [0]
    trace = TrainingTrace(clock=lambda: now[0])
    requests = []
    async def wake_early(delay):
        requests.append(delay)
        now[0] = 40_000_000 if len(requests) == 1 else 100_000_000
    monkeypatch.setattr(asyncio, "sleep", wake_early)
    result = asyncio.run(wait_for_deadline(trace, 100_000_000))
    assert requests == pytest.approx([.1, .06])
    assert now[0] == 100_000_000 and result["early_wakeups"] == 1 and result["sleep_calls"] == 2
    sleeps = trace.export()["records"]
    assert sleeps[0]["woke_early"] and not sleeps[1]["woke_early"]


def test_deadline_wait_preserves_cancellation(monkeypatch):
    trace = TrainingTrace(clock=lambda: 0)
    async def cancelled(delay):
        raise asyncio.CancelledError
    monkeypatch.setattr(asyncio, "sleep", cancelled)
    with pytest.raises(asyncio.CancelledError):
        asyncio.run(wait_for_deadline(trace, 100_000_000))
    record = trace.export()["records"][-1]
    assert record["name"] == "tick.sleep" and record["error_code"] == "CancelledError"


def test_scheduler_profile_smoke_keeps_raw_samples_and_isolated_clock_control(tmp_path):
    from src.services.scheduler_profile import run as profile
    policy = asyncio.get_event_loop_policy()
    result = profile(tmp_path/"scheduler", samples=20, warmup=0, repeats=1)
    assert result["profiling_status"] == "PASS" and result["acceptance_status"] == "NOT_ASSESSED"
    assert len(result["cases"]) == 4 and len(result["comparisons"]) == 2
    assert asyncio.get_event_loop_policy() is policy
    for case in result["cases"]:
        assert case["lateness"]["samples"] == 20 and case["trace"]["dropped_records"] == 0
        raw = (tmp_path/"scheduler"/case["case"]/"raw-timings.jsonl").read_text().splitlines()
        assert len(raw) == case["trace"]["total_records"]
        assert len(case["selected_schedule_sequences"]) == 20
        if case["clock_mode"] == "COUNTER_CONTROL":
            assert case["clock_inventory"]["loop_clock_resolution_s"] == case["clock_inventory"]["perf_counter"]["resolution"]


def test_scheduler_profile_excludes_declared_warmup_and_retains_outliers():
    from src.services.scheduler_profile import summarize_case
    records = [{"name": "tick.schedule", "outcome": "OK", "running": True, "sequence": index+1,
        "start_ns": (index+1)*50_000_000, "wait_started_ns": index*50_000_000,
        "lateness_ms": lateness, "boundary_ms": 2, "sleep_calls": 1, "early_wakeups": 0,
        "missed_deadlines": 0, "wait_enter_lateness_ms": 0}
        for index, lateness in enumerate((9999, 1, 1000))]
    measured, values = summarize_case(records, samples=2, warmup=1)
    assert values == [1, 1000] and measured["lateness"]["p99_ms"] == 1000
    assert measured["selected_schedule_sequences"] == [2, 3]
    with pytest.raises(ValueError, match="declared sample"):
        summarize_case(records, samples=4, warmup=0)


def test_scheduler_timer_request_is_released_when_profiling_fails():
    from src.services.scheduler_profile import timer_period_control
    calls = []
    api = SimpleNamespace(timeBeginPeriod=lambda period: calls.append(("begin", period)) or 0,
                          timeEndPeriod=lambda period: calls.append(("end", period)) or 0)
    with pytest.raises(RuntimeError, match="profiling failed"):
        with timer_period_control(True, api=api) as info:
            assert info["requested_ms"] == 1 and info["released"] is None
            raise RuntimeError("profiling failed")
    assert calls == [("begin", 1), ("end", 1)] and info["released"] is True


def test_scheduler_timer_request_failure_does_not_release_an_unowned_request():
    from src.services.scheduler_profile import timer_period_control
    api = SimpleNamespace(timeBeginPeriod=lambda period: 1,
                          timeEndPeriod=lambda period: pytest.fail("Unowned timer request was released"))
    with pytest.raises(OSError, match="request failed"):
        with timer_period_control(True, api=api):
            pytest.fail("A failed timer request entered its profiling scope")


def test_diagnostic_runner_keeps_raw_evidence_and_incomplete_gate(tmp_path):
    output = tmp_path/"benchmark"
    result = run(output, ticks=2, validation_samples=1, reconnect_samples=1, manual_clock=True)
    assert result["diagnostic_status"] == "PASS" and result["acceptance_status"] == "BLOCKED"
    assert result["p1_gates"] == "NOT_RUN" and result["trace"]["dropped_records"] == 0
    assert result["backend_integration_status"] == "PASS"
    assert result["parameters"]["clock_mode"] == "MANUAL_TEST"
    assert result["tick_schedule"]["lateness"] is None
    assert result["real_time_factor"]["status"] == "NOT_MEASURED_MANUAL_CLOCK"
    assert result["committed_state_age"]["status"] == "NOT_MEASURED_MANUAL_CLOCK"
    records = [json.loads(line) for line in (output/"raw-timings.jsonl").read_text().splitlines()]
    assert len(records) == result["trace"]["total_records"]
    assert {"session.recovery", "runtime.session_recovery", "route.validation", "snapshot.commit"} <= {r["name"] for r in records}
    assert any(r.get("command_type") == "RESERVE_CHARGER" and r["outcome"] == "APPLIED" for r in records)
    assert any(r["name"] == "command.accept" and r.get("error_code") == "IDEMPOTENCY_CONFLICT" for r in records)
    progress = [r for r in records if r["name"] == "runtime.progress"]
    assert any(r["oldest_committed_state_age_ms"] is not None for r in progress)
    assert any(r["session_epoch"] > 1 and r["committed_state_age_ms"] is None for r in progress)
    phases = result["integration_trace"]
    assert {p["session_id"] for p in phases} == {result["session_id"]}
    assert sum(p["phase"] == "recovered" for p in phases) == 2
    assert {"route_approved", "incident_detected", "incident_resolved", "charging_started",
            "charging_progress", "charging_finished", "completed"} <= {p["phase"] for p in phases}
    replay = next(p["events"] for p in phases if p["phase"] == "event_replay")
    assert sum(e["event_type"] == "CHARGING_FINISHED" for e in replay) == 1
    assert {e["event_type"] for e in replay} >= {"DEPOT_BAY_ARRIVED", "INCIDENT_DECISION_RECORDED", "ROUTE_APPROVED"}
    route_sources = result["file_sha256"]
    assert {"backend/fixtures/m2_integrated.json", "backend/src/services/integrated_actor.py",
            "backend/src/services/training_workflow.py"} <= route_sources.keys()
    assert all(r["owner"] and r["remediation_gate"] for r in result["open_risks"])
    with pytest.raises(ValueError, match="never overwritten"):
        run(output, ticks=2, validation_samples=1, reconnect_samples=1)


def test_diagnostic_miss_preserves_measured_stage_without_invented_cause():
    record = {"name": "tick.compute", "outcome": "OK", "duration_ms": 99,
              "sequence": 1, "session_id": "s", "session_epoch": 1, "tick": 1}
    result = diagnostic_budgets([record], {"tick_compute_ms": {"p95": 10, "p99": 25}})
    assert result[0]["status"] == "MISS" and result[0]["small_sample"]
    assert result[0]["slowest_records"][0]["sequence"] == 1


def test_runner_failure_leaves_raw_and_failure_artifacts(tmp_path, monkeypatch):
    def fail(*args):
        raise RuntimeError("private failure details")
    monkeypatch.setattr("src.services.training_benchmark.command", fail)
    with pytest.raises(RuntimeError):
        run(tmp_path/"failed", ticks=2, validation_samples=1, reconnect_samples=1, manual_clock=True)
    failure = json.loads((tmp_path/"failed/failure.json").read_bytes())
    assert failure["diagnostic_status"] == "FAIL" and failure["error_code"] == "RuntimeError"
    assert (tmp_path/"failed/raw-timings.jsonl").stat().st_size > 0
    assert json.loads((tmp_path/"failed/integration-trace.json").read_bytes())[0]["phase"] == "created"
    assert "private failure" not in json.dumps(failure)


@pytest.mark.parametrize("diagnostic_only,passed,expected", [(False, True, 2), (True, True, 0), (True, False, 1)])
def test_cli_cannot_report_incomplete_acceptance_as_success(monkeypatch, diagnostic_only, passed, expected):
    monkeypatch.setattr("sys.argv", ["benchmark", "--output", "unused"] + (["--diagnostic-only"] if diagnostic_only else []))
    monkeypatch.setattr("src.services.training_benchmark.run", lambda *args, **kwargs: {
        "profile": "P0", "diagnostic_status": "PASS" if passed else "FAIL", "acceptance_status": "BLOCKED", "tick_schedule": {}})
    assert main() == expected
