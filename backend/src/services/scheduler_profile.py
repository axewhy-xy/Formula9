"""
Controlled scheduler profiling for the BE-M2-06 integrated backend.

Author: Nur Syazliyarni Binte Zazali
Date Created: 28/10/2026
Last Modified: 28/10/2026

Description:
    Compares the unchanged scheduler wait policy on the default event loop and
    an isolated performance-counter clock control, first without actor work and
    then with the real integrated actor/SQLite commits. Alternates run order,
    retains raw records, warmup/sample identities and clock/source metadata.
    The optional Windows timer-period control pairs a process-local 1 ms request
    with its release. Neither control changes production event-loop policy,
    persistent OS settings or dependencies, or establishes P1 acceptance.
"""

import argparse
import asyncio
from collections import Counter
from contextlib import contextmanager
import ctypes
from datetime import UTC, datetime
import json
from pathlib import Path
import sys
import time
from uuid import uuid4

from fastapi.testclient import TestClient

from src.api.routes.training_v2 import get_actor_id, get_training_store
from src.app import app
from src.core.training_telemetry import TrainingTrace
from src.persistence.training_store import TrainingStore
from src.services.integrated_actor import SCENARIO_SELECTOR
from src.services.session_actor import SessionActor
from src.services.training_benchmark import command, source_metadata, summary
from src.services.training_sessions import wait_for_deadline


def counter_control_loop():
    """Change a disposable loop's clock/resolution for this experiment only.

    _clock_resolution is a CPython implementation detail. Keeping this private
    override here prevents an unvalidated timing experiment from becoming an
    application-wide policy or production dependency.
    """
    loop = asyncio.new_event_loop()
    loop.time = time.perf_counter
    loop._clock_resolution = time.get_clock_info("perf_counter").resolution
    return loop


@contextmanager
def timer_period_control(enabled=False, *, api=None):
    """Balance a diagnostic process-local timer request, including on failure.

    Reject older Windows versions where timeBeginPeriod changes global timer
    resolution. An injected API supports cleanup tests without OS changes.
    """
    info = {"requested_ms": None, "released": None}
    if not enabled:
        yield info
        return
    if api is None:
        if sys.platform != "win32" or sys.getwindowsversion().build < 19041:
            raise ValueError("Timer-period control requires Windows 10 version 2004 or newer")
        api = ctypes.WinDLL("winmm")
        for name in ("timeBeginPeriod", "timeEndPeriod"):
            method = getattr(api, name)
            method.argtypes = [ctypes.c_uint]
            method.restype = ctypes.c_uint
    if api.timeBeginPeriod(1) != 0:
        raise OSError("Diagnostic timer-period request failed")
    info["requested_ms"] = 1
    try:
        yield info
    finally:
        info["released"] = api.timeEndPeriod(1) == 0
        if not info["released"]:
            raise OSError("Diagnostic timer-period release failed")


async def clock_inventory():
    loop = asyncio.get_running_loop()
    return {"event_loop": type(loop).__name__, "loop_clock_resolution_s": loop._clock_resolution,
            "monotonic": vars(time.get_clock_info("monotonic")),
            "perf_counter": vars(time.get_clock_info("perf_counter"))}


def summarize_case(records, *, samples, warmup):
    """Keep the first fixed sample window after warmup; retain all raw outliers."""
    schedule = [r for r in records if r["name"] == "tick.schedule" and r["running"]]
    chosen = schedule[warmup:warmup+samples]
    if len(chosen) != samples:
        raise ValueError("Scheduler profile did not collect its declared sample count")
    first = chosen[0]["wait_started_ns"]
    last = chosen[-1]["start_ns"] + round(chosen[-1]["boundary_ms"]*1e6)
    components = {}
    for name in ("tick.sleep", "tick.lock_wait", "tick.worker_wait", "tick.stage", "tick.actor",
                 "snapshot.build", "snapshot.commit", "writer.queue_wait", "writer.transaction"):
        values = [r["duration_ms"] for r in records if r["name"] == name
                  and r["outcome"] == "OK" and first <= r["start_ns"] <= last]
        if values:
            components[name] = summary(values)
    return {"lateness": summary([r["lateness_ms"] for r in chosen]),
            "boundary": summary([r["boundary_ms"] for r in chosen]),
            "wait_enter_lateness": summary([r["wait_enter_lateness_ms"] for r in chosen]),
            "sleep_calls": sum(r["sleep_calls"] for r in chosen),
            "early_wakeups": sum(r["early_wakeups"] for r in chosen),
            "late_after_positive_wait": sum(r["lateness_ms"] > 10 and r["wait_enter_lateness_ms"] == 0
                                            for r in chosen),
            "boundaries_over_50ms": sum(r["boundary_ms"] > 50 for r in chosen),
            "missed_deadlines": sum(r["missed_deadlines"] for r in chosen),
            "selected_schedule_sequences": [r["sequence"] for r in chosen],
            "components": components}, [r["lateness_ms"] for r in chosen]


async def wait_only(trace, count):
    """Use the production wait helper and deadline policy with zero actor work."""
    inventory = await clock_inventory()
    step_ns = round(SessionActor.STEP_S*1e9)
    deadline = trace.clock()+step_ns
    for tick in range(count):
        fields = await wait_for_deadline(trace, deadline, session_id="wait-only", session_epoch=1,
                                         tick=tick+1, running=True)
        start = trace.clock()
        end = trace.clock()
        trace.record("tick.schedule", start, start, session_id="wait-only", session_epoch=1,
                     tick=tick+1, running=True, lateness_ms=max(0, start-deadline)/1e6,
                     boundary_ms=(end-start)/1e6, missed_deadlines=max(0, (end-deadline)//step_ns), **fields)
        deadline = max(deadline+step_ns, trace.clock())
    return inventory


def actor_case(directory, trace, count, factory):
    previous = dict(app.dependency_overrides)
    try:
        with TrainingStore(directory/"runtime.sqlite", trace=trace) as store:
            app.dependency_overrides[get_training_store] = lambda: store
            app.dependency_overrides[get_actor_id] = lambda: "scheduler-profiler"
            options = {"loop_factory": factory} if factory else {}
            with TestClient(app, backend_options=options) as client:
                inventory = client.portal.call(clock_inventory)
                def post(path, body, status=200, **kwargs):
                    result = client.post(path, json=body, **kwargs)
                    if result.status_code != status:
                        raise AssertionError(f"Profiling HTTP request failed: {result.status_code}")
                    return result.json()
                created = post("/api/v2/sessions", {"scenario_revision_id": SCENARIO_SELECTOR, "seed": 1000},
                               status=201, headers={"Idempotency-Key": "scheduler-profile"})
                session_id = created["session_id"]
                path = f"/api/v2/sessions/{session_id}"
                preview = post(path+"/route-previews", {"route_id": "route-m2-detour",
                    "expected_route_revision": 1, "draft_revision": 1})
                approval = post(path+"/route-approvals", {"preview_id": preview["preview_id"],
                    "preview_revision": preview["preview_revision"], "command_id": uuid4().hex,
                    "idempotency_key": "scheduler-route", "target_vehicle_ids": ["shuttle-01"],
                    "reason": "Profile the integrated actor on its admitted bypass"})
                if approval["status"] != "APPLIED":
                    raise AssertionError("Profiling route was not applied")
                started = post(path+"/commands", command(store.snapshot(session_id), "START").model_dump(mode="json"), status=202)
                if started["status"] != "APPLIED":
                    raise AssertionError("Profiling actor did not start")
                deadline = time.monotonic()+max(10, count*.2)
                while (current := store.snapshot(session_id)).tick < count:
                    if current.session.status != "RUNNING" or time.monotonic() > deadline:
                        raise TimeoutError("Profiling actor failed or stopped advancing")
                    time.sleep(.05)
                paused = post(path+"/commands", command(current, "PAUSE").model_dump(mode="json"), status=202)
                if paused["status"] != "APPLIED":
                    raise AssertionError("Profiling actor did not pause")
                inventory["scenario_revision"] = current.scenario.revision_id
                inventory["session_id"] = session_id
        return inventory
    finally:
        app.dependency_overrides.clear()
        app.dependency_overrides.update(previous)


def run(output, *, samples=400, warmup=20, repeats=2, timer_period_controls=False):
    """Run paired clock controls sequentially so the cases do not compete."""
    if (type(samples) is not int or not 20 <= samples <= 2000
            or type(warmup) is not int or not 0 <= warmup <= 200
            or type(repeats) is not int or not 1 <= repeats <= 3 or type(timer_period_controls) is not bool):
        raise ValueError("Scheduler profiling sample counts are outside their bounds")
    output = Path(output)
    if output.exists() and any(output.iterdir()):
        raise ValueError("Profiling output must be new or empty; evidence is never overwritten")
    output.mkdir(parents=True, exist_ok=True)
    metadata = source_metadata()
    cases, pools = [], {}
    modes = ["DEFAULT", "COUNTER_CONTROL"] + (["TIMER_PERIOD_CONTROL"] if timer_period_controls else [])
    def write(path, value):
        path.write_text(json.dumps(value, indent=2, sort_keys=True)+"\n", encoding="utf-8")
    write(output/"manifest.json", {**metadata, "samples_per_case": samples, "warmup_samples": warmup,
          "repeats": repeats, "step_s": SessionActor.STEP_S, "timer_period_controls": timer_period_controls,
          "control_scope": "Disposable loop and optional balanced process-local timer request; no production policy changes"})
    try:
        for workload in ("WAIT_ONLY", "INTEGRATED_ACTOR"):
            for repeat in range(repeats):
                order = modes if repeat % 2 == 0 else list(reversed(modes))
                for clock_mode in order:
                    name = f"{workload.lower()}-{repeat+1}-{clock_mode.lower()}"
                    directory = output/name
                    directory.mkdir()
                    trace = TrainingTrace(capacity=65536)
                    factory = counter_control_loop if clock_mode == "COUNTER_CONTROL" else None
                    inventory = None
                    try:
                        with timer_period_control(clock_mode == "TIMER_PERIOD_CONTROL") as period:
                            if workload == "WAIT_ONLY":
                                with asyncio.Runner(loop_factory=factory) as runner:
                                    inventory = runner.run(wait_only(trace, samples+warmup))
                            else:
                                inventory = actor_case(directory, trace, samples+warmup+8, factory)
                    finally:
                        exported = trace.export()
                        records = exported.pop("records")
                        (directory/"raw-timings.jsonl").write_text(
                            "".join(json.dumps(r, sort_keys=True)+"\n" for r in records), encoding="utf-8")
                        write(directory/"trace-metadata.json", exported)
                    if exported["dropped_records"]:
                        raise AssertionError("Profiling trace overflowed")
                    measured, latencies = summarize_case(records, samples=samples, warmup=warmup)
                    case = {"case": name, "workload": workload, "clock_mode": clock_mode, "repeat": repeat+1,
                        "clock_inventory": inventory, "timer_period": period, "trace": exported, **measured,
                        "error_counts": dict(Counter(r.get("error_code", r["outcome"]) for r in records
                                                     if r.get("error_code") is not None or r["outcome"] in {
                                                         "ERROR", "HTTP_ERROR", "TRANSPORT_ERROR"}))}
                    cases.append(case)
                    pools.setdefault((workload, clock_mode), []).extend(latencies)
                    write(directory/"summary.json", case)
                    print(json.dumps({"case": name, "p95_lateness_ms": measured["lateness"]["p95_ms"],
                                      "early_wakeups": measured["early_wakeups"]}), flush=True)
        comparisons = [{"workload": workload, **{mode.lower(): summary(pools[(workload, mode)]) for mode in modes},
                        "small_sample_per_case": samples < 1000}
                       for workload in ("WAIT_ONLY", "INTEGRATED_ACTOR")]
        result = {"artifact_version": 1, "task": "BE-M2-06", "profile": "SCHEDULER_CLOCK_ATTRIBUTION",
            "recorded_at_utc": datetime.now(UTC).isoformat(), **metadata,
            "profiling_status": "PASS", "acceptance_status": "NOT_ASSESSED",
            "samples_per_case": samples, "warmup_samples": warmup, "repeats": repeats,
            "timer_period_controls": timer_period_controls,
            "comparisons": comparisons, "cases": cases,
            "limitations": "P0 sequential timer experiment; no visible clients, P1 content, production policy change or dependency upgrade"}
        write(output/"summary.json", result)
        return result
    except BaseException as error:
        write(output/"failure.json", {"profiling_status": "FAIL", "error_code": type(error).__name__, "cases": cases})
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--samples", type=int, default=400)
    parser.add_argument("--warmup", type=int, default=20)
    parser.add_argument("--repeats", type=int, default=2)
    parser.add_argument("--timer-period-controls", action="store_true",
                        help="Include balanced process-local 1 ms timer requests on supported Windows")
    args = parser.parse_args()
    result = run(args.output, samples=args.samples, warmup=args.warmup, repeats=args.repeats,
                 timer_period_controls=args.timer_period_controls)
    print(json.dumps(result["comparisons"], indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
