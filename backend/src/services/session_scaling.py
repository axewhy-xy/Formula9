"""
Multi-session scaling baseline.

Author: Lau Xiang Yang
Date Created: 17/11/2026
Last Modified: 17/11/2026

Description:
    Phase 0 of the multi-core sessions plan: how many live integrated sessions one backend process keeps
    at 1x, and where each tick's time goes as the count grows. For every session count N it creates N
    integrated sessions (with or without background traffic) in a fresh SQLite store, starts them, lets
    them warm up, then measures a window in which every session advances at least the requested number
    of ticks. Optionally one client polls one session's snapshot once a second and long-polls its traffic,
    as the Unity client does, so the cost of request handling on the same core shows up too.

    Per session it records the real-time factor (simulated seconds over wall seconds), tick schedule
    lateness and missed deadlines; per case the cost split of the existing trace spans, the process's
    CPU use and memory. The output names the first N at which some session falls below 0.99x.

    Sessions and HTTP run in this process through the in-process ASGI transport, as the other backend
    diagnostics do. The polling client's own request and JSON work shares the GIL with the sessions here,
    where the real client runs in another process, so client-load cases are slightly pessimistic.

    The `ab` command runs the same sweep alternately against two source trees (by default this checkout
    and a clean copy of a git ref), so a change can be compared on a laptop whose speed drifts between
    runs. Each side runs as its own process with a copy of this harness.

    Run from the repository root:
        $env:PYTHONPATH = 'backend;.'
        & .\\backend\\.venv\\Scripts\\python.exe -m src.services.session_scaling run --output .tools/scaling
"""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from datetime import UTC, datetime
import hashlib
import io
import json
import math
import os
from pathlib import Path
import platform
import shutil
import statistics
import subprocess
import sys
import tarfile
import threading
from time import monotonic, perf_counter_ns, process_time, sleep

SPANS = ("tick.boundary", "tick.compute", "tick.stage", "tick.actor", "tick.worker_wait", "tick.lock_wait",
         "snapshot.build", "snapshot.commit", "writer.queue_wait", "writer.transaction", "traffic.frame")
# Work a tick does on behalf of its session, outside waiting for its deadline: what a session costs
TICK_WORK = ("tick.compute", "snapshot.build", "snapshot.commit", "traffic.frame")
REFERENCE_FACTOR = 0.99
DEFAULT_SESSIONS = (1, 2, 4, 6, 8, 12, 16)
# As the Unity client: SessionController.TrafficWait, and Formula9App.PollTraffic's idle pause
TRAFFIC_WAIT_MS = 1000
TRAFFIC_IDLE_S = 0.05


def stats(values):
    """Nearest-rank p50/p95/p99, max and mean of a sample, or None for none."""
    if not values:
        return None
    ordered = sorted(values)
    def rank(p):
        return ordered[max(0, math.ceil(p * len(ordered)) - 1)]
    return {"samples": len(ordered), "mean": math.fsum(ordered) / len(ordered), "p50": rank(.50),
            "p95": rank(.95), "p99": rank(.99), "max": ordered[-1]}


def host_details():
    """What the machine was doing the run on: cores, power source and CPU name, where known."""
    details = {"os": platform.platform(), "python": platform.python_version(), "logical_cpu_count": os.cpu_count(),
               "physical_cores": None, "cpu": platform.processor() or None, "power_source": None}
    if sys.platform == "win32":
        try:
            out = subprocess.run(["powershell", "-NoProfile", "-Command",
                "$p = Get-CimInstance Win32_Processor; ($p | Measure-Object NumberOfCores -Sum).Sum; $p[0].Name"],
                capture_output=True, text=True, timeout=20, check=True).stdout.splitlines()
            details["physical_cores"] = int(out[0].strip())
            details["cpu"] = out[1].strip() or details["cpu"]
        except (OSError, subprocess.SubprocessError, ValueError, IndexError):
            pass
        try:
            import ctypes

            class PowerStatus(ctypes.Structure):
                _fields_ = [("ac", ctypes.c_byte), ("flag", ctypes.c_byte), ("percent", ctypes.c_byte),
                            ("saver", ctypes.c_byte), ("life", ctypes.c_ulong), ("full", ctypes.c_ulong)]
            status = PowerStatus()
            if ctypes.windll.kernel32.GetSystemPowerStatus(ctypes.byref(status)):
                details["power_source"] = {0: "BATTERY", 1: "AC"}.get(status.ac, "UNKNOWN")
                details["battery_saver"] = bool(status.saver)
        except (OSError, AttributeError):
            pass
    else:
        try:
            details["physical_cores"] = len({line for line in Path("/proc/cpuinfo").read_text().splitlines()
                                             if line.startswith("core id")}) or None
        except OSError:
            pass
    return details


def memory_mib():
    """This process's working set and its peak so far, in MiB, where the platform tells."""
    if sys.platform == "win32":
        try:
            import ctypes
            from ctypes import wintypes

            class Counters(ctypes.Structure):
                _fields_ = [("cb", wintypes.DWORD), ("faults", wintypes.DWORD),
                            ("peak", ctypes.c_size_t), ("current", ctypes.c_size_t),
                            *((name, ctypes.c_size_t) for name in ("a", "b", "c", "d", "e", "f"))]
            counters = Counters()
            counters.cb = ctypes.sizeof(counters)
            kernel32, psapi = ctypes.WinDLL("kernel32"), ctypes.WinDLL("psapi")
            kernel32.GetCurrentProcess.restype = wintypes.HANDLE
            psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
            psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
            if psapi.GetProcessMemoryInfo(kernel32.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
                return {"working_set_mib": counters.current / 2**20, "peak_working_set_mib": counters.peak / 2**20}
        except (OSError, AttributeError):
            pass
        return {"working_set_mib": None, "peak_working_set_mib": None}
    import resource
    return {"working_set_mib": None, "peak_working_set_mib": resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024}


class ClientLoad:
    """One client on one session, as the Unity client: its snapshot once a second, and its traffic
    long-polled, asking again as soon as each reply comes. A session without traffic has none to ask
    for, and the 3D view does not keep asking."""

    def __init__(self, client, session_id, *, traffic=True):
        self.client = client
        self.path = f"/api/v2/sessions/{session_id}"
        self.stop = threading.Event()
        self.snapshots = []  # (start_ns, duration_ms, status)
        self.traffic = []  # (start_ns, duration_ms, status, frames)
        self.frames = []  # (received_ns, tick)
        self.errors = Counter()
        self.threads = [threading.Thread(target=self._guard, args=(target,), daemon=True, name=name)
                        for name, target in (("client-snapshot", self._poll_snapshot),
                                             *((("client-traffic", self._poll_traffic),) if traffic else ()))]

    def __enter__(self):
        for thread in self.threads:
            thread.start()
        return self

    def __exit__(self, *exc):
        self.stop.set()
        for thread in self.threads:
            thread.join(timeout=TRAFFIC_WAIT_MS / 1000 + 5)

    def _guard(self, target):
        try:
            target()
        except Exception as error:  # A client failure is evidence, not a reason to lose the case
            self.errors[type(error).__name__] += 1

    def _poll_snapshot(self):
        due = monotonic()
        while not self.stop.is_set():
            start = perf_counter_ns()
            response = self.client.get(self.path + "/snapshot")
            self.snapshots.append((start, (perf_counter_ns() - start) / 1e6, response.status_code))
            if response.status_code != 200:
                self.errors[f"snapshot_{response.status_code}"] += 1
            due += 1.0
            self.stop.wait(max(0.0, due - monotonic()))

    def _poll_traffic(self):
        after = -1
        while not self.stop.is_set():
            start = perf_counter_ns()
            response = self.client.get(self.path + "/traffic", params={"after_tick": after, "wait_ms": TRAFFIC_WAIT_MS})
            end = perf_counter_ns()
            frames = response.json()["frames"] if response.status_code == 200 else []
            self.traffic.append((start, (end - start) / 1e6, response.status_code, len(frames)))
            if response.status_code != 200:
                self.errors[f"traffic_{response.status_code}"] += 1
                self.stop.wait(0.25)
                continue
            for frame in frames:
                self.frames.append((end, frame["tick"]))
                after = max(after, frame["tick"])
            if not frames:
                self.stop.wait(TRAFFIC_IDLE_S)


def analyse_client(load, start_ns, end_ns):
    """The client's view of the window: request latencies, and how many traffic frames reached it."""
    wall_s = (end_ns - start_ns) / 1e9
    inside = [(t, tick) for t, tick in load.frames if start_ns <= t < end_ns]
    ticks = [tick for _, tick in inside]
    arrivals = [(b - a) / 1e6 for (a, _), (b, _) in zip(inside, inside[1:]) if b > a]
    return {"snapshot_latency_ms": stats([ms for t, ms, status, *_ in load.snapshots
                                          if start_ns <= t < end_ns and status == 200]),
            "traffic_latency_ms": stats([ms for t, ms, status, _ in load.traffic
                                         if start_ns <= t < end_ns and status == 200]),
            "traffic_frames_per_s": len(inside) / wall_s if wall_s else None,
            # Ten frames a second is 1x; fewer is the slow motion a 3D view draws
            "traffic_frame_tick_span": (max(ticks) - min(ticks)) if ticks else None,
            "traffic_reply_gap_ms": stats(arrivals),
            "errors": dict(load.errors)}


def analyse(records, *, session_ids, start_ns, end_ns, step_s, cpu_s=None):
    """Summarize one case's measurement window from its trace records.

    A session's real-time factor is its simulated advance between its last progress observations before
    the window's start and before its end, over the wall time between them. Spans count when they start
    inside the window. Where spans nest (tick.compute in tick.boundary, writer.transaction in
    snapshot.commit), their busy shares overlap and must not be added up.
    """
    wall_s = (end_ns - start_ns) / 1e9
    if wall_s <= 0:
        raise ValueError("Measurement window is empty")
    ids = set(session_ids)
    by_name = defaultdict(list)
    for record in records:
        by_name[record["name"]].append(record)
    progress = defaultdict(list)
    for record in by_name["runtime.progress"]:
        if record.get("session_id") in ids:
            progress[record["session_id"]].append(record)
    inside = lambda r: start_ns <= r["start_ns"] < end_ns
    schedule = [r for r in by_name["tick.schedule"] if r.get("running") and r.get("session_id") in ids and inside(r)]
    per_session = {}
    for session_id in session_ids:
        points = sorted(progress[session_id], key=lambda r: r["end_ns"])
        before = [r for r in points if r["end_ns"] <= start_ns]
        until = [r for r in points if r["end_ns"] <= end_ns]
        first, last = (before[-1], until[-1]) if before and until else (None, None)
        factor = ticks = None
        if first is not None and last["end_ns"] > first["end_ns"] and last["session_epoch"] == first["session_epoch"]:
            ticks = last["tick"] - first["tick"]
            factor = ticks * step_s / ((last["end_ns"] - first["end_ns"]) / 1e9)
        own = [r for r in schedule if r["session_id"] == session_id]
        per_session[session_id] = {"real_time_factor": factor, "ticks": ticks,
            "lateness_ms": stats([r["lateness_ms"] for r in own]),
            "missed_deadlines": sum(r["missed_deadlines"] for r in own)}
    factors = [s["real_time_factor"] for s in per_session.values() if s["real_time_factor"] is not None]
    spans = {}
    for name in SPANS:
        durations = [r["duration_ms"] for r in by_name[name] if r["outcome"] == "OK" and inside(r)
                     and (r.get("session_id") in ids or not name.startswith("tick."))]
        if durations:
            spans[name] = {**stats(durations), "busy_share": math.fsum(durations) / 1000 / wall_s}
    http = defaultdict(list)
    for r in by_name["http.publication"]:
        if inside(r):
            http[(r["method"], r["route"], r["status_code"])].append(r)
    requests = [{"method": method, "route": route, "status_code": status, **stats([r["duration_ms"] for r in rs]),
                 "busy_share": math.fsum(r["duration_ms"] for r in rs) / 1000 / wall_s,
                 "response_kib": stats([r["response_bytes"] / 1024 for r in rs])}
                for (method, route, status), rs in sorted(http.items())]
    work = math.fsum(spans[name]["mean"] * spans[name]["samples"] for name in TICK_WORK if name in spans)
    return {"wall_s": wall_s, "sessions": len(session_ids),
        "real_time_factor": {"min": min(factors) if factors else None,
                             "median": statistics.median(factors) if factors else None,
                             "max": max(factors) if factors else None,
                             "below_reference": sum(f < REFERENCE_FACTOR for f in factors),
                             "unmeasured_sessions": len(session_ids) - len(factors)},
        "lateness_ms": stats([r["lateness_ms"] for r in schedule]),
        "missed_deadlines": sum(r["missed_deadlines"] for r in schedule),
        "running_ticks": len(schedule),
        # Per session tick: comparable with the 5 ms (desktop) / 14.5 ms (laptop) noted on
        # MAX_TRAFFIC_SESSIONS at N=1; above that it also holds time spent waiting for the GIL
        "work_ms_per_tick": work / len(schedule) if schedule else None,
        "process_cpu_cores": cpu_s / wall_s if cpu_s is not None else None,
        "spans": spans, "http": requests, "per_session": per_session}


def run_case(directory, *, sessions, traffic, client_load, ticks, warmup_s, seed, timeout_s, keep_raw=False):
    """Run N live integrated sessions in a fresh store and measure one window. Returns the case summary."""
    from fastapi.testclient import TestClient

    from src.api.routes.training_v2 import get_actor_id, get_training_store
    from src.app import app
    from src.core.training_telemetry import TrainingTrace
    from src.persistence.training_store import TrainingStore
    from src.services.integrated_actor import SCENARIO_SELECTOR, IntegratedSessionActor
    from src.services.session_actor import SessionActor
    from src.services.training_benchmark import command
    from src.services.training_sessions import TrainingSessionService

    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=False)
    # Records come in at about 20 a session tick, paused or running; the cap only bounds a runaway case
    trace = TrainingTrace(capacity=max(65536, sessions * 20 * 20 * int(warmup_s + timeout_s + 120)))
    previous = dict(app.dependency_overrides)
    caps = (TrainingSessionService.MAX_SESSIONS, TrainingSessionService.MAX_TRAFFIC_SESSIONS,
            IntegratedSessionActor.TRAFFIC)
    session_ids, window, load, cpu_s, ticks_at = [], None, None, None, {}
    timings = {}
    try:
        # The admission caps are what this measures, so they must not refuse the sessions it asks for
        TrainingSessionService.MAX_SESSIONS = max(caps[0], sessions)
        TrainingSessionService.MAX_TRAFFIC_SESSIONS = max(caps[1], sessions)
        IntegratedSessionActor.TRAFFIC = traffic
        with TrainingStore(directory / "runtime.sqlite", trace=trace) as store:
            app.dependency_overrides[get_training_store] = lambda: store
            app.dependency_overrides[get_actor_id] = lambda: "scaling-benchmark"
            with TestClient(app) as client:
                def post(path, body, expected, **kwargs):
                    response = client.post(path, json=body, **kwargs)
                    if response.status_code != expected:
                        raise AssertionError(f"POST {path}: expected {expected}, received {response.status_code}: "
                                             f"{response.text[:300]}")
                    return response.json()
                started = monotonic()
                for index in range(sessions):
                    created = post("/api/v2/sessions", {"scenario_revision_id": SCENARIO_SELECTOR, "seed": seed + index},
                                   201, headers={"Idempotency-Key": f"scaling-{index}"})
                    session_ids.append(created["session_id"])
                timings["create_s"] = monotonic() - started
                service = app.state.training_services[store]
                for session_id in session_ids:
                    result = post(f"/api/v2/sessions/{session_id}/commands",
                                  command(store.snapshot(session_id), "START").model_dump(mode="json"), 202)
                    if result["status"] != "APPLIED":
                        raise AssertionError(f"Session {session_id} did not start: {result['status']}")
                traffic_live = service.traffic_sessions()

                def current_ticks():
                    live = service.sessions
                    failed = [sid for sid in session_ids if sid not in live or live[sid].failed]
                    if failed:
                        raise AssertionError(f"Sessions stopped during the run: {failed}")
                    return {sid: live[sid].actor.current_tick for sid in session_ids}

                load = ClientLoad(client, session_ids[0], traffic=traffic_live > 0) if client_load else None
                if load is not None:
                    load.__enter__()
                try:
                    sleep(warmup_s)
                    ticks_at["start"] = current_ticks()
                    cpu_started, wall_started = process_time(), monotonic()
                    window = [trace.clock(), None]
                    complete = False
                    while monotonic() - wall_started < timeout_s:
                        sleep(0.25)
                        now = current_ticks()
                        if all(now[sid] - ticks_at["start"][sid] >= ticks for sid in session_ids):
                            complete = True
                            break
                    window[1] = trace.clock()
                    cpu_s = process_time() - cpu_started
                    ticks_at["end"] = current_ticks()
                    memory = memory_mib()
                finally:
                    if load is not None:
                        load.__exit__(None, None, None)
                incidents = {sid: len(store.snapshot(sid).incidents) for sid in session_ids}
        exported = trace.export()
        records = exported.pop("records")
        if keep_raw:
            with open(directory / "raw-timings.jsonl", "w", encoding="utf-8") as raw:
                raw.writelines(json.dumps(r, sort_keys=True) + "\n" for r in records)
        measured = analyse(records, session_ids=session_ids, start_ns=window[0], end_ns=window[1],
                           step_s=SessionActor.STEP_S, cpu_s=cpu_s)
        for session_id in session_ids:
            measured["per_session"][session_id]["incidents_at_end"] = incidents[session_id]
            measured["per_session"][session_id]["final_tick"] = ticks_at["end"][session_id]
        case = {"sessions": sessions, "traffic": traffic, "traffic_sessions_live": traffic_live,
                "client_load": client_load, "seeds": [seed + i for i in range(sessions)],
                "minimum_window_ticks": ticks, "warmup_s": warmup_s, "timeout_s": timeout_s,
                "window_complete": complete, "timings": timings, "memory": memory,
                "trace": {key: exported[key] for key in ("total_records", "dropped_records", "capacity", "high_water")},
                **measured}
        if load is not None:
            case["client"] = analyse_client(load, *window)
        if exported["dropped_records"]:
            case["warning"] = "Trace overflowed; early records are missing"
        return case
    finally:
        app.dependency_overrides.clear()
        app.dependency_overrides.update(previous)
        (TrainingSessionService.MAX_SESSIONS, TrainingSessionService.MAX_TRAFFIC_SESSIONS,
         IntegratedSessionActor.TRAFFIC) = caps


def run_case_process(directory, *, sessions, traffic, client_load, ticks, warmup_s, seed, timeout_s, keep_raw=False):
    """run_case in a fresh Python process with this one's interpreter and source tree."""
    import contracts
    import src
    # The child runs this file by path, so it finds the packages through PYTHONPATH, whatever this
    # process was started with
    roots = {str(Path(src.__file__).resolve().parents[1]), str(Path(contracts.__file__).resolve().parents[1])}
    environment = {**os.environ, "PYTHONPATH": os.pathsep.join([*sorted(roots),
                                                                 *filter(None, [os.environ.get("PYTHONPATH")])])}
    subprocess.run([sys.executable, str(Path(__file__).resolve()), "case", "--output", str(directory),
                    "--sessions", str(sessions), "--traffic", "on" if traffic else "off",
                    "--client", "on" if client_load else "off", "--ticks", str(ticks), "--warmup", str(warmup_s),
                    "--seed", str(seed), "--timeout", str(timeout_s), *(["--keep-raw"] if keep_raw else [])],
                   env=environment, check=True)
    return json.loads((Path(directory) / "summary.json").read_bytes())


def _modes(value):
    return {"on": (True,), "off": (False,), "both": (True, False)}[value]


def _case_name(round_index, sessions, traffic, client_load):
    return f"r{round_index + 1}-n{sessions:02d}-{'traffic' if traffic else 'notraffic'}-{'client' if client_load else 'noclient'}"


def breakpoints(cases):
    """Per traffic/client mode: the first session count at which a session runs below 0.99x, and the
    largest count below that at which every session kept up. Several rounds count by their median."""
    grouped = defaultdict(lambda: defaultdict(list))
    for case in cases:
        grouped[(case["traffic"], case["client_load"])][case["sessions"]].append(case["real_time_factor"]["min"])
    result = []
    for (traffic, client_load), by_count in sorted(grouped.items(), reverse=True):
        medians = {n: statistics.median(v) if None not in v else None for n, v in sorted(by_count.items())}
        behind = next((n for n, f in medians.items() if f is None or f < REFERENCE_FACTOR), None)
        kept = [n for n, f in medians.items() if f is not None and f >= REFERENCE_FACTOR and (behind is None or n < behind)]
        result.append({"traffic": traffic, "client_load": client_load, "median_min_factor": medians,
                       "first_count_below_reference": behind, "largest_count_at_reference": max(kept) if kept else None})
    return result


def _fmt(value, digits=2):
    return "–" if value is None else f"{value:.{digits}f}"


def report(result):
    """The sweep as Markdown tables, for reading without tooling."""
    host = result["host"]
    lines = [f"# Session scaling baseline ({result['recorded_at_utc'][:16]} UTC)", "",
             f"Source {result.get('source_commit') or 'unknown'}{' (dirty)' if result.get('source_dirty') else ''}. "
             f"Host: {host.get('cpu')}, {host.get('physical_cores')} cores / {host.get('logical_cpu_count')} threads, "
             f"power {host.get('power_source')}, Python {host.get('python')}.", "",
             f"Window: every session advances at least {result['parameters']['ticks']} ticks after "
             f"{result['parameters']['warmup_s']} s of warm-up. Real-time factor (RTF) below "
             f"{REFERENCE_FACTOR} means the session runs in slow motion.", "", "## Where sessions fall behind", "",
             "| Traffic | Client | Last N at 1x | First N below 1x |", "| --- | --- | --- | --- |"]
    for row in result["breakpoints"]:
        lines.append(f"| {'on' if row['traffic'] else 'off'} | {'on' if row['client_load'] else 'off'} | "
                     f"{row['largest_count_at_reference'] or 'none'} | {row['first_count_below_reference'] or 'not reached'} |")
    lines += ["", "## Cases", "",
              "| Case | RTF min | RTF median | Lateness p50 / p95 / p99 ms | Missed | Work ms/tick | CPU cores | Frames/s at client | Complete |",
              "| --- | --- | --- | --- | --- | --- | --- | --- | --- |"]
    for case in result["cases"]:
        late = case["lateness_ms"] or {}
        client = case.get("client") or {}
        lines.append(f"| {case['case']} | {_fmt(case['real_time_factor']['min'], 3)} | "
                     f"{_fmt(case['real_time_factor']['median'], 3)} | {_fmt(late.get('p50'), 1)} / "
                     f"{_fmt(late.get('p95'), 1)} / {_fmt(late.get('p99'), 1)} | {case['missed_deadlines']} | "
                     f"{_fmt(case['work_ms_per_tick'])} | {_fmt(case['process_cpu_cores'])} | "
                     f"{_fmt(client.get('traffic_frames_per_s'), 1)} | {'yes' if case['window_complete'] else 'timed out'} |")
    names = ("tick.stage", "tick.actor", "snapshot.build", "snapshot.commit", "writer.transaction", "traffic.frame",
             "tick.worker_wait")
    lines += ["", "## Cost split", "",
              "Mean ms per span, and in brackets the share of one core it kept busy over the window. "
              "snapshot.commit includes waiting for writer.transaction; tick.worker_wait is time a tick waited "
              "for a thread.", "", "| Case | " + " | ".join(names) + " |", "| --- |" + " --- |" * len(names)]
    for case in result["cases"]:
        cells = [f"{_fmt(case['spans'][n]['mean'])} ({_fmt(case['spans'][n]['busy_share'])})" if n in case["spans"]
                 else "–" for n in names]
        lines.append(f"| {case['case']} | " + " | ".join(cells) + " |")
    http = [(case["case"], r) for case in result["cases"] for r in case["http"]]
    if http:
        lines += ["", "## Request handling", "", "| Case | Route | Requests | Mean ms | p95 ms | Core share |",
                  "| --- | --- | --- | --- | --- | --- |"]
        lines += [f"| {name} | {r['method']} {r['route']} ({r['status_code']}) | {r['samples']} | {_fmt(r['mean'])} | "
                  f"{_fmt(r['p95'])} | {_fmt(r['busy_share'], 3)} |" for name, r in http]
        lines.append("")
        lines.append("Long-polled /traffic requests spend most of their time waiting for a frame, so their "
                     "duration is not work.")
    return "\n".join(lines) + "\n"


def _write(path, value):
    Path(path).write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def run(output, *, session_counts=DEFAULT_SESSIONS, traffic="on", client="on", ticks=600, warmup_s=5.0,
        rounds=1, seed=1000, timeout_s=None, keep_raw=False, isolate=True,
        progress=lambda line: print(line, flush=True)):
    """Sweep the session counts in each traffic/client mode, alternating their order between rounds so a
    machine's drift spreads across them, and write summary.json and report.md.

    With isolate, each case runs in a process of its own, as a server would: in one long process every
    case inherits the heap the cases before it left (which slows the collector's full passes) and the
    memory they grew, so later cases measured slower than the same case run alone."""
    from src.services.integrated_actor import get_workflow_definition
    from src.services.training_benchmark import source_metadata
    from src.services.training_sessions import freeze_static_heap

    session_counts = tuple(session_counts)
    if (not session_counts or any(type(n) is not int or not 1 <= n <= 32 for n in session_counts)
            or type(ticks) is not int or not 20 <= ticks <= 12000 or not 0 <= warmup_s <= 120
            or type(rounds) is not int or not 1 <= rounds <= 10 or traffic not in ("on", "off", "both")
            or client not in ("on", "off", "both") or (timeout_s is not None and timeout_s <= 0)):
        raise ValueError("Scaling parameters are outside their bounds")
    timeout_s = timeout_s if timeout_s is not None else max(60.0, ticks * 0.05 * 10)
    output = Path(output)
    if output.exists() and any(output.iterdir()):
        raise ValueError("Output directory must be new or empty; existing evidence is never overwritten")
    output.mkdir(parents=True, exist_ok=True)
    metadata = source_metadata()
    harness = Path(__file__).read_bytes()
    host = host_details()
    parameters = {"session_counts": list(session_counts), "traffic": traffic, "client": client, "ticks": ticks,
                  "warmup_s": warmup_s, "rounds": rounds, "seed": seed, "timeout_s": timeout_s,
                  "transport": "IN_PROCESS_ASGI", "process_per_case": isolate, "harness_sha256": hashlib.sha256(harness).hexdigest(),
                  # Sessions open as the scenario sets them; the workflow benchmark forces this opener on
                  "tutorial_opening": get_workflow_definition().incidents.simulated_catalog.policy
                                      ["tutorial_opening"]["enabled"]}
    if ticks < 240:
        parameters["note"] = "Windows shorter than about 214 ticks miss the first random incident's publish path"
    _write(output / "manifest.json", {**metadata, "host": host, "parameters": parameters})
    if not isolate:
        # Load and freeze the shared static data before the first case, so it is not timed in any case
        freeze_static_heap()
    cases = []
    try:
        for round_index in range(rounds):
            counts = session_counts if round_index % 2 == 0 else tuple(reversed(session_counts))
            for traffic_on in _modes(traffic):
                for client_on in _modes(client):
                    for sessions in counts:
                        name = _case_name(round_index, sessions, traffic_on, client_on)
                        options = dict(sessions=sessions, traffic=traffic_on, client_load=client_on, ticks=ticks,
                                       warmup_s=warmup_s, seed=seed, timeout_s=timeout_s, keep_raw=keep_raw)
                        measured = (run_case_process(output / name, **options) if isolate
                                    else run_case(output / name, **options))
                        case = {"case": name, "round": round_index + 1, **measured}
                        _write(output / name / "summary.json", case)
                        cases.append(case)
                        late = case["lateness_ms"] or {}
                        progress(json.dumps({"case": name, "rtf_min": case["real_time_factor"]["min"],
                                             "lateness_p95_ms": late.get("p95"),
                                             "work_ms_per_tick": case["work_ms_per_tick"],
                                             "cpu_cores": case["process_cpu_cores"]}))
        result = {"artifact_version": 1, "profile": "MULTI_SESSION_SCALING_P0",
                  "recorded_at_utc": datetime.now(UTC).isoformat(), **metadata, "host": host,
                  "parameters": parameters, "breakpoints": breakpoints(cases), "cases": cases}
        _write(output / "summary.json", result)
        (output / "report.md").write_text(report(result), encoding="utf-8")
        return result
    except BaseException as error:
        _write(output / "failure.json", {"status": "FAIL", "error_code": type(error).__name__,
                                          "completed_cases": [c["case"] for c in cases]})
        raise


def extract_ref(repository, ref, destination):
    """A clean copy of the backend and contracts at a git ref, as the A/B baseline."""
    archive = subprocess.run(["git", "archive", "--format=tar", ref, "backend", "contracts"], cwd=repository,
                             capture_output=True, check=True).stdout
    destination.mkdir(parents=True)
    with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
        tar.extractall(destination, filter="data")
    return destination


def ab(output, *, baseline_root=None, baseline_ref=None, candidate_root=None, rounds=3, run_args=(),
       python=sys.executable):
    """Run the sweep alternately on a baseline and a candidate source tree, each round in the other order,
    with the same copy of this harness, and compare the medians."""
    output = Path(output)
    if output.exists() and any(output.iterdir()):
        raise ValueError("Output directory must be new or empty; existing evidence is never overwritten")
    if (baseline_root is None) == (baseline_ref is None) or type(rounds) is not int or not 1 <= rounds <= 10:
        raise ValueError("Give one baseline (a root or a git ref) and 1-10 rounds")
    here = Path(__file__).resolve().parents[3]
    candidate_root = Path(candidate_root or here).resolve()
    output.mkdir(parents=True, exist_ok=True)
    if baseline_ref is not None:
        baseline_root = extract_ref(candidate_root, baseline_ref, output / "baseline-src")
    roots = {"baseline": Path(baseline_root).resolve(), "candidate": candidate_root}
    harness = output / "harness" / "session_scaling.py"
    harness.parent.mkdir()
    shutil.copy2(__file__, harness)
    runs = defaultdict(list)
    for round_index in range(rounds):
        for side in (("baseline", "candidate") if round_index % 2 == 0 else ("candidate", "baseline")):
            target = output / f"round-{round_index + 1}-{side}"
            environment = {**os.environ, "PYTHONPATH": os.pathsep.join([str(roots[side] / "backend"), str(roots[side])])}
            print(json.dumps({"round": round_index + 1, "side": side, "root": str(roots[side])}), flush=True)
            subprocess.run([python, str(harness), "run", "--output", str(target), *run_args],
                           cwd=roots[side], env=environment, check=True)
            runs[side].append(json.loads((target / "summary.json").read_bytes()))
    comparison = compare(runs)
    _write(output / "ab-summary.json", {"roots": {k: str(v) for k, v in roots.items()}, "baseline_ref": baseline_ref,
                                        "rounds": rounds, "comparison": comparison})
    (output / "ab-report.md").write_text(ab_report(comparison, baseline_ref or str(roots["baseline"])), encoding="utf-8")
    return comparison


def compare(runs):
    """Median over rounds of each case's minimum RTF, p95 lateness and work per tick, per side."""
    table = defaultdict(lambda: defaultdict(lambda: defaultdict(list)))
    for side, results in runs.items():
        for result in results:
            for case in result["cases"]:
                key = (case["sessions"], case["traffic"], case["client_load"])
                values = table[key][side]
                values["rtf_min"].append(case["real_time_factor"]["min"])
                values["lateness_p95_ms"].append((case["lateness_ms"] or {}).get("p95"))
                values["work_ms_per_tick"].append(case["work_ms_per_tick"])
    def median(values):
        values = [v for v in values if v is not None]
        return statistics.median(values) if values else None
    rows = []
    for (sessions, traffic, client_load), sides in sorted(table.items(), key=lambda item: (not item[0][1], not item[0][2], item[0][0])):
        row = {"sessions": sessions, "traffic": traffic, "client_load": client_load}
        for side, values in sides.items():
            row[side] = {metric: median(v) for metric, v in values.items()}
            row[side]["rounds"] = len(values["rtf_min"])
        base, cand = row.get("baseline", {}).get("work_ms_per_tick"), row.get("candidate", {}).get("work_ms_per_tick")
        row["work_ratio"] = cand / base if base and cand else None
        rows.append(row)
    return rows


def ab_report(rows, baseline):
    lines = [f"# Session scaling A/B: candidate against {baseline}", "",
             "Medians over rounds. Work ratio below 1 means the candidate's ticks cost less.", "",
             "| N | Traffic | Client | RTF min base / cand | Lateness p95 ms base / cand | Work ms/tick base / cand | Work ratio |",
             "| --- | --- | --- | --- | --- | --- | --- |"]
    for row in rows:
        base, cand = row.get("baseline", {}), row.get("candidate", {})
        lines.append(f"| {row['sessions']} | {'on' if row['traffic'] else 'off'} | {'on' if row['client_load'] else 'off'} | "
                     f"{_fmt(base.get('rtf_min'), 3)} / {_fmt(cand.get('rtf_min'), 3)} | "
                     f"{_fmt(base.get('lateness_p95_ms'), 1)} / {_fmt(cand.get('lateness_p95_ms'), 1)} | "
                     f"{_fmt(base.get('work_ms_per_tick'))} / {_fmt(cand.get('work_ms_per_tick'))} | "
                     f"{_fmt(row['work_ratio'], 3)} |")
    return "\n".join(lines) + "\n"


def _add_run_options(parser):
    parser.add_argument("--sessions", default=",".join(map(str, DEFAULT_SESSIONS)),
                        help="Comma-separated session counts (default %(default)s)")
    parser.add_argument("--ticks", type=int, default=600,
                        help="Ticks every session must advance inside the window (default %(default)s)")
    parser.add_argument("--warmup", type=float, default=5.0, help="Seconds of running before the window")
    parser.add_argument("--traffic", choices=("on", "off", "both"), default="on")
    parser.add_argument("--client", choices=("on", "off", "both"), default="on",
                        help="One client polling one session's snapshot and traffic during the window")
    parser.add_argument("--seed", type=int, default=1000)
    parser.add_argument("--timeout", type=float, help="Longest window in seconds (default ten times its 1x length)")
    parser.add_argument("--keep-raw", action="store_true", help="Also write each case's raw trace records")


def _run_options(args):
    options = ["--sessions", args.sessions, "--ticks", str(args.ticks), "--warmup", str(args.warmup),
               "--traffic", args.traffic, "--client", args.client, "--seed", str(args.seed)]
    if args.timeout is not None:
        options += ["--timeout", str(args.timeout)]
    return options + (["--keep-raw"] if args.keep_raw else [])


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    sweep = commands.add_parser("run", help="Sweep session counts on this source tree")
    sweep.add_argument("--output", type=Path, required=True)
    sweep.add_argument("--rounds", type=int, default=1)
    sweep.add_argument("--in-process", action="store_true",
                       help="Run every case in this process rather than one process a case (for debugging)")
    _add_run_options(sweep)
    single = commands.add_parser("case", help="One case in this process; used by run")
    single.add_argument("--output", type=Path, required=True)
    _add_run_options(single)
    versus = commands.add_parser("ab", help="Alternate the sweep between a baseline and this source tree")
    versus.add_argument("--output", type=Path, required=True)
    baseline = versus.add_mutually_exclusive_group(required=True)
    baseline.add_argument("--baseline-ref", help="Git ref to copy as the baseline, for example HEAD")
    baseline.add_argument("--baseline-root", type=Path, help="Repository root of an existing baseline copy")
    versus.add_argument("--candidate-root", type=Path, help="Repository root to test (default this checkout)")
    versus.add_argument("--rounds", type=int, default=3)
    _add_run_options(versus)
    args = parser.parse_args(argv)
    try:
        counts = tuple(int(n) for n in args.sessions.split(","))
    except ValueError:
        parser.error("--sessions takes comma-separated integers")
    if args.command == "case":
        if len(counts) != 1 or "both" in (args.traffic, args.client) or args.timeout is None:
            parser.error("case takes one session count, on/off modes and a timeout")
        measured = run_case(args.output, sessions=counts[0], traffic=args.traffic == "on",
                            client_load=args.client == "on", ticks=args.ticks, warmup_s=args.warmup, seed=args.seed,
                            timeout_s=args.timeout, keep_raw=args.keep_raw)
        _write(args.output / "summary.json", measured)
        return 0
    if args.command == "ab":
        rows = ab(args.output, baseline_root=args.baseline_root, baseline_ref=args.baseline_ref,
                  candidate_root=args.candidate_root, rounds=args.rounds, run_args=_run_options(args))
        print((args.output / "ab-report.md").read_text(encoding="utf-8"))
        return 0 if rows else 1
    result = run(args.output, session_counts=counts, traffic=args.traffic, client=args.client, ticks=args.ticks,
                 warmup_s=args.warmup, rounds=args.rounds, seed=args.seed, timeout_s=args.timeout,
                 keep_raw=args.keep_raw, isolate=not args.in_process)
    print(json.dumps(result["breakpoints"], indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
