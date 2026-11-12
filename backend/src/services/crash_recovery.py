"""
Isolated process-crash recovery measurements for BE-M2-06.

Author: Nur Syazliyarni Binte Zazali
Date Created: 28/10/2026
Last Modified: 28/10/2026

Description:
    Kills only helper processes created by this harness, using separate SQLite
    databases and ephemeral loopback ports/credentials. Fault gates interrupt a
    running tick transaction, durable acceptance, or committed application before
    its HTTP reply. Real application startup, authentication and HTTP reconciliation
    verify PAUSED recovery, private workflow state and exactly-once effects.
    Accelerated fixture preparation is outside all measured recovery intervals.
    Raw evidence is retained on failure; P0 measurements do not establish P1.
    Diagnostic routes and fault hooks exist only in the explicitly launched helper.
"""

import argparse
from contextlib import closing
from datetime import UTC, datetime, timedelta
import hashlib
import json
import os
from pathlib import Path
import secrets
import socket
import sqlite3
import subprocess
import sys
import sysconfig
from threading import Event, Thread
from time import monotonic, perf_counter_ns, sleep
from uuid import uuid4

import httpx

ROOT = Path(__file__).resolve().parents[3]
ACTOR = "crash-probe-operator"
CASES = {
    "hold_uncommitted_tick": ("HOLD", "UNCOMMITTED_TICK"),
    "charging_uncommitted_tick": ("CHARGING", "UNCOMMITTED_TICK"),
    "accepted_before_apply": ("HOLD", "BEFORE_APPLY"),
    "committed_before_reply": ("CHARGING", "AFTER_APPLY"),
}


def write(path, value):
    """Publish complete JSON evidence atomically, including fault-gate markers."""
    temporary = path.with_suffix(path.suffix+".tmp")
    temporary.write_text(json.dumps(value, indent=2, sort_keys=True)+"\n", encoding="utf-8")
    temporary.replace(path)


def wait_signal(path, process, deadline):
    """Read a published IPC marker despite a short Windows sharing lock.

    File replacement makes JSON publication atomic, but a newly visible path
    can still be temporarily inaccessible. Permanent denial fails at the same
    bounded readiness deadline; it never permits killing an unverified process.
    """
    last_error = None
    while monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError("Isolated backend exited before publishing its marker; see server.log")
        try:
            return json.loads(path.read_bytes())
        except (FileNotFoundError, PermissionError) as error:
            last_error = error
        sleep(.005)
    raise TimeoutError("Isolated backend marker was not readable before its deadline") from last_error


def durable_state(database):
    """Read the committed journal without opening TrainingStore/recovering it."""
    with closing(sqlite3.connect(database.resolve().as_uri()+"?mode=ro", uri=True)) as db:
        snapshot = db.execute("SELECT snapshot_json FROM sessions").fetchone()
        runtime = db.execute("SELECT checkpoint_json FROM session_runtimes").fetchone()
        commands = [json.loads(row[0]) for row in db.execute("SELECT command_json FROM commands ORDER BY rowid")]
        receipts = [dict(zip(("command_id", "status", "applied_tick", "result_event_id", "target_revision"), row))
                    for row in db.execute("SELECT command_id,status,applied_tick,result_event_id,target_revision FROM commands ORDER BY rowid")]
        events = [json.loads(row[0]) for row in db.execute("SELECT event_json FROM events ORDER BY event_sequence")]
        return {"snapshot": json.loads(snapshot[0]), "runtime": json.loads(runtime[0]),
                "commands": commands, "receipts": receipts, "events": events,
                "integrity": db.execute("PRAGMA integrity_check").fetchone()[0]}


def request(client, method, path, *, expected=200, **kwargs):
    response = client.request(method, path, **kwargs)
    if response.status_code != expected:
        # Response bodies can contain credentials from unrelated auth failures.
        raise AssertionError(f"{method} {path}: expected {expected}, received {response.status_code}")
    return response.json()


def command(snapshot, kind):
    return {"contract_version": "2.0", "command_id": uuid4().hex, "idempotency_key": uuid4().hex,
            "session_id": snapshot["session_id"], "command_type": kind,
            "expected_epoch": snapshot["session_epoch"],
            "expected_revision": snapshot["session"]["aggregate_revision"], "target_type": "SESSION",
            "target_id": snapshot["session_id"], "payload": {}, "reason": "Isolated crash-recovery drill"}


def prepare(output, phases):
    """Prepare real integrated hold/charge checkpoints with explicit manual steps."""
    from fastapi.testclient import TestClient
    from src.api.routes.training_v2 import get_actor_id, get_training_store
    from src.app import app
    from src.persistence.training_store import TrainingStore
    from src.services.training_benchmark import _ManualSessionService
    from src.services.integrated_actor import SCENARIO_SELECTOR, IntegratedSessionActor, get_workflow_definition

    previous = dict(app.dependency_overrides)
    seeds = {}
    # The drill holds its shuttle at the scenario's tutorial road block, which new sessions only open
    # with when the scenario turns it on
    opening = get_workflow_definition().incidents.simulated_catalog.policy["tutorial_opening"]
    opening_enabled = opening["enabled"]
    opening["enabled"] = True
    # It holds the shuttle where the incident was detected, which a session does only without background
    # traffic: with it, the shuttle drives on until the closure stops it. A restored session keeps the
    # traffic its checkpoint has, none.
    traffic = IntegratedSessionActor.TRAFFIC
    IntegratedSessionActor.TRAFFIC = False
    try:
        with TrainingStore(output/"preparation.sqlite") as store:
            app.dependency_overrides[get_training_store] = lambda: store
            app.dependency_overrides[get_actor_id] = lambda: ACTOR
            service = _ManualSessionService(store)
            app.state.training_services = {store: service}
            with TestClient(app) as client:
                created = request(client, "POST", "/api/v2/sessions", expected=201,
                    headers={"Idempotency-Key": "crash-fixture"},
                    json={"scenario_revision_id": SCENARIO_SELECTOR, "seed": 1000})
                sid = created["session_id"]
                path = f"/api/v2/sessions/{sid}"

                def snapshot():
                    return store.snapshot(sid).model_dump(mode="json")

                def send(body):
                    result = request(client, "POST", path+"/commands", expected=202, json=body)
                    assert result["status"] == "APPLIED"

                # Real Singapore trips (shuttle-05 to its bus interchange is about 2.6 km) take longer than 4400 ticks
                async def advance_until(predicate, limit=12000):
                    for _ in range(limit//2):
                        if predicate(snapshot()):
                            return
                        await service.step(service.sessions[sid])
                        await service.step(service.sessions[sid])
                    raise AssertionError("Fixture did not reach the requested workflow phase")

                def save(phase):
                    send(command(snapshot(), "PAUSE"))
                    target = output/(phase.lower()+"-seed.sqlite")
                    with closing(sqlite3.connect(store.path)) as source, closing(sqlite3.connect(target)) as dest:
                        source.backup(dest)
                    seeds[phase] = {"database": str(target), "session_id": sid, "route_approval": approval,
                                    "preparation_clock": "MANUAL_TEST_NOT_MEASURED"}
                    write(output/(phase.lower()+"-seed.json"), seeds[phase])

                preview = request(client, "POST", path+"/route-previews", json={
                    "route_id": "route-m2-detour", "expected_route_revision": 1, "draft_revision": 1})
                approval = {"preview_id": preview["preview_id"], "preview_revision": preview["preview_revision"],
                    "command_id": uuid4().hex, "idempotency_key": "crash-route",
                    "target_vehicle_ids": ["shuttle-01"], "reason": "Authored crash drill bypass"}
                assert request(client, "POST", path+"/route-approvals", json=approval)["status"] == "APPLIED"
                send(command(snapshot(), "START"))
                current = snapshot()
                option = next(o for o in current["charge_options"] if o["action"] == "CHARGE_NOW")
                charger = next(c for c in current["chargers"] if c["charger_id"] == option["charger_id"])
                battery = command(current, "RESERVE_CHARGER")
                battery.update(target_type="CHARGER", target_id=charger["charger_id"],
                    expected_revision=charger["aggregate_revision"],
                    payload={"option_id": option["option_id"], "option_revision": option["revision"]})
                send(battery)
                client.portal.call(advance_until, lambda s: bool(s["incidents"]))
                current = snapshot()
                incident = current["incidents"][0]
                candidate = next(c for c in current["candidates"] if c["action"] == "WAIT")
                wait = command(current, "APPROVE_INCIDENT_OPTION")
                wait.update(target_type="INCIDENT", target_id=incident["incident_id"],
                    expected_revision=incident["aggregate_revision"], payload={"candidate_id": candidate["candidate_id"]})
                send(wait)
                if "HOLD" in phases:
                    save("HOLD")
                    send(command(snapshot(), "RESUME"))
                if "CHARGING" in phases:
                    client.portal.call(advance_until, lambda s: s["vehicles"][4]["service_state"] == "CHARGING")
                    target_tick = snapshot()["tick"]+20
                    client.portal.call(advance_until, lambda s: s["tick"] >= target_tick, 40)
                    save("CHARGING")
    finally:
        opening["enabled"] = opening_enabled
        IntegratedSessionActor.TRAFFIC = traffic
        app.dependency_overrides.clear()
        app.dependency_overrides.update(previous)
    return seeds


def serve(database, output):
    """Internal helper only: real app/auth/lifespan plus authenticated fault controls."""
    import uvicorn
    from fastapi import Depends
    from src.api.routes.training_v2 import get_actor_id, get_training_store
    from src.app import app
    from src.auth.service import AuthService, set_auth_service
    from src.auth.store import MemoryAccountStore
    from src.services.training_sessions import checkpoint

    accounts = MemoryAccountStore()
    now = datetime.now(UTC)
    token = os.environ.pop("FORMULA9_CRASH_PROBE_TOKEN")
    accounts.add_session({"token_hash": hashlib.sha256(token.encode()).hexdigest(), "username": ACTOR,
                         "created_at": now, "expires_at": now+timedelta(hours=1)})
    set_auth_service(AuthService(accounts))
    store = get_training_store()
    assert store.path.resolve() == database.resolve()
    armed = {}

    def gate(*, transaction=None):
        exported = store.trace.export()
        records = exported.pop("records")
        (output/"raw-timings.jsonl").write_text("".join(json.dumps(r, sort_keys=True)+"\n" for r in records), encoding="utf-8")
        write(output/"trace-metadata.json", exported)
        attempted = json.loads(transaction.execute("SELECT snapshot_json FROM sessions").fetchone()[0]) if transaction else None
        write(output/"fault-reached.json", {"fault": armed["fault"], "pid": os.getpid(),
              "at_ns": perf_counter_ns(), "transaction_open": bool(transaction and transaction.in_transaction),
              "attempted_tick": attempted["tick"] if attempted else None})
        # This thread never releases normally: the owning parent kills this process.
        Event().wait(30)
        raise TimeoutError("Crash probe fault gate was not terminated")

    original_runtime = store._write_runtime
    original_apply = store.apply

    def write_runtime(connection, sid, state):
        original_runtime(connection, sid, state)
        if armed.get("fault") == "UNCOMMITTED_TICK" and state["tick"] > armed["tick"]:
            gate(transaction=connection)

    def apply(cid, *args, **kwargs):
        if cid == armed.get("command_id") and armed.get("fault") == "BEFORE_APPLY":
            gate()
        result = original_apply(cid, *args, **kwargs)
        if cid == armed.get("command_id") and armed.get("fault") == "AFTER_APPLY":
            gate()
        return result

    store._write_runtime = write_runtime
    store.apply = apply
    server = uvicorn.Server(uvicorn.Config(app, log_level="warning", access_log=False))

    @app.post("/_crash_probe/arm")
    async def arm(body: dict, actor: str = Depends(get_actor_id)):
        assert actor == ACTOR and not armed and body["fault"] in {v[1] for v in CASES.values()}
        armed.update(body)
        return {"armed": True}

    @app.get("/_crash_probe/state")
    async def state(actor: str = Depends(get_actor_id)):
        assert actor == ACTOR
        service = app.state.training_services[store]
        session = next(iter(service.sessions.values()))
        async with session.lock:
            # As the store keeps it: the traffic's own checkpoint is apart (TrainingStore.runtime)
            return {"runtime": checkpoint(session.actor, traffic=False), "is_paused": session.actor.is_paused,
                    "session_count": len(service.sessions), "trace": store.trace.export()}

    @app.post("/_crash_probe/stop")
    async def stop(actor: str = Depends(get_actor_id)):
        assert actor == ACTOR
        server.should_exit = True
        return {"stopping": True}

    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        listener.listen()
        write(output/"listener.json", {"pid": os.getpid(), "port": listener.getsockname()[1]})
        server.run(sockets=[listener])


class Backend:
    """Own one ephemeral helper and kill only its direct subprocess handle."""
    def __init__(self, database, output):
        self.output = output
        output.mkdir()
        self.log = (output/"server.log").open("w", encoding="utf-8")
        token = secrets.token_urlsafe(32)
        # Windows venv python.exe is a redirector that creates a second process.
        # Launch its underlying interpreter with this environment's package path
        # so the Popen handle owns the actual server that must be killed.
        interpreter = sys._base_executable if os.name == "nt" else sys.executable
        env = {**os.environ, "PYTHONPATH": os.pathsep.join((str(ROOT/"backend"), str(ROOT), sysconfig.get_path("purelib"))),
               "PYTHONDONTWRITEBYTECODE": "1", "TRAINING_DATABASE_PATH": str(database.resolve()),
               "MONGODB_URI": "", "FORMULA9_CRASH_PROBE_TOKEN": token}
        self.started_ns = perf_counter_ns()
        self.process = subprocess.Popen([interpreter, "-m", "src.services.crash_recovery", "--serve",
            "--database", str(database.resolve()), "--output", str(output.resolve())], cwd=ROOT,
            env=env, stdout=self.log, stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        self.client = httpx.Client(headers={"Authorization": f"Bearer {token}"}, timeout=2, trust_env=False)

    def ready(self):
        deadline = monotonic()+20
        info = wait_signal(self.output/"listener.json", self.process, deadline)
        assert info["pid"] == self.process.pid
        self.client.base_url = f"http://127.0.0.1:{info['port']}"
        while monotonic() < deadline:
            if self.process.poll() is not None:
                raise RuntimeError("Isolated backend exited before becoming ready; see server.log")
            try:
                if self.client.get("/").status_code == 200:
                    return
            except httpx.TransportError:
                pass
            sleep(.01)
        raise TimeoutError("Isolated backend did not become ready")

    def kill(self):
        start = perf_counter_ns()
        self.process.kill()
        code = self.process.wait(timeout=10)
        return {"requested_ns": start, "exited_ns": perf_counter_ns(), "exit_code": code,
                "pid": self.process.pid, "method": "TerminateProcess" if os.name == "nt" else "SIGKILL"}

    def close(self):
        try:
            if self.process.poll() is None:
                try:
                    self.client.post("/_crash_probe/stop")
                    self.process.wait(timeout=5)
                except (httpx.HTTPError, subprocess.TimeoutExpired):
                    self.kill()
        finally:
            self.client.close()
            self.log.close()
            write(self.output/"cleanup.json", {"pid": self.process.pid,
                  "exit_code": self.process.poll(), "exited": self.process.poll() is not None})


def verify(before, after, private):
    """Reject lost/duplicated receipts, events or execution state after recovery."""
    old, new = before["snapshot"], after["snapshot"]
    assert after["integrity"] == "ok"
    assert new["session_id"] == old["session_id"] and new["session_epoch"] == old["session_epoch"]+1
    assert new["session"]["status"] == "PAUSED" and new["tick"] == old["tick"]
    assert private["is_paused"] and private["session_count"] == 1
    excluded = {"session_epoch", "session", "event_sequence", "snapshot_id", "state_hash"}
    assert {k:v for k,v in new.items() if k not in excluded} == {k:v for k,v in old.items() if k not in excluded}
    assert after["runtime"] == before["runtime"]  # Recovery must not rewrite private committed progress.
    expected = before["runtime"]
    actual = private["runtime"]
    assert {k:v for k,v in expected.items() if k != "workflow"} == {k:v for k,v in actual.items() if k != "workflow"}
    assert {k:v for k,v in expected["workflow"].items() if k != "domain"} == {
        k:v for k,v in actual["workflow"].items() if k != "domain"}
    assert {k:v for k,v in actual["workflow"]["domain"].items() if k not in excluded} == {
        k:v for k,v in new.items() if k not in excluded}
    events = after["events"]
    assert events[:len(before["events"])] == before["events"]
    assert [e["event_sequence"] for e in events] == list(range(1, new["event_sequence"]+1))
    assert len({e["event_id"] for e in events}) == len(events)
    pending = sum(r["status"] == "QUEUED" for r in before["receipts"])
    assert len(events)-len(before["events"]) == pending+1
    assert events[-1]["event_type"] == "SESSION_RECOVERED"
    reconfirmed = events[len(before["events"]):-1]
    assert all(e["event_type"] == "COMMAND_NEEDS_RECONFIRMATION" for e in reconfirmed)
    assert {e["entity_id"] for e in reconfirmed} == {
        r["command_id"] for r in before["receipts"] if r["status"] == "QUEUED"}
    assert after["commands"] == before["commands"]
    for old_receipt, new_receipt in zip(before["receipts"], after["receipts"], strict=True):
        if old_receipt["status"] == "QUEUED":
            assert new_receipt["command_id"] == old_receipt["command_id"]
            assert new_receipt["status"] == "NEEDS_RECONFIRMATION" and new_receipt["applied_tick"] is None
        else:
            assert new_receipt == old_receipt


def trial(directory, seed, fault):
    directory.mkdir()
    database = directory/"runtime.sqlite"
    with closing(sqlite3.connect(seed["database"])) as source, closing(sqlite3.connect(database)) as dest:
        source.backup(dest)
    processes = []
    pending_thread = None
    try:
        first = Backend(database, directory/"before-crash")
        processes.append(first)
        first.ready()
        client = first.client
        path = f"/api/v2/sessions/{seed['session_id']}"
        restored_seed = request(client, "GET", path+"/snapshot")
        resume = command(restored_seed, "RESUME")
        assert request(client, "POST", path+"/commands", expected=202, json=resume)["status"] == "APPLIED"
        current = request(client, "GET", path+"/snapshot")
        interrupted = command(current, "PAUSE")
        request(client, "POST", "/_crash_probe/arm", json={"fault": fault,
            "tick": current["tick"], "command_id": interrupted["command_id"]})
        transport = {}
        if fault != "UNCOMMITTED_TICK":
            def submit():
                try:
                    # Gate timeout exceeds the parent's kill deadline.
                    reply = client.post(path+"/commands", json=interrupted, timeout=25)
                    transport["http_status"] = reply.status_code
                except httpx.HTTPError as error:
                    transport["error_code"] = type(error).__name__
            pending_thread = Thread(target=submit)
            pending_thread.start()
        marker = first.output/"fault-reached.json"
        gate = wait_signal(marker, first.process, monotonic()+10)
        assert gate["pid"] == first.process.pid and gate["fault"] == fault
        before = durable_state(database)
        assert before["integrity"] == "ok"
        if fault == "UNCOMMITTED_TICK":
            assert before["snapshot"]["session"]["status"] == "RUNNING"
            assert gate["transaction_open"] and gate["attempted_tick"] > before["snapshot"]["tick"]
        else:
            observed = request(client, "GET", path+f"/commands/{interrupted['idempotency_key']}")
            assert observed["status"] == ("QUEUED" if fault == "BEFORE_APPLY" else "APPLIED")
            write(directory/"observed-receipt.json", observed)
        write(directory/"before.json", before)
        crash = first.kill()
        assert crash["exit_code"] != 0
        # No evidence export or user restart is inserted into this measured interval.
        second = Backend(database, directory/"after-crash")
        processes.append(second)
        second.ready()
        if pending_thread:
            assert not pending_thread.is_alive() and "http_status" not in transport
        client = second.client
        response = client.get(path+"/snapshot")
        assert response.status_code == 200 and len(response.headers.get_list("x-training-trace-id")) == 1
        public = response.json()
        snapshot_ns = perf_counter_ns()
        private = request(client, "GET", "/_crash_probe/state")
        after = durable_state(database)
        assert public == after["snapshot"]
        verify(before, after, private)
        for receipt, body in zip(after["receipts"], after["commands"], strict=True):
            observed = request(client, "GET", path+f"/commands/{body['idempotency_key']}")
            assert all(observed[k] == v for k,v in receipt.items())
        public_events = request(client, "GET", path+"/events", params={"after": 0, "limit": 100})
        assert not public_events["has_more"] and public_events["events"] == after["events"]
        # Two public reads separated by live scheduler time establish that recovery stays paused.
        sleep(.15)
        assert request(client, "GET", path+"/snapshot") == public
        verified_ns = perf_counter_ns()
        write(directory/"after.json", {**after, "live_runtime": private})

        for body in after["commands"]:
            retry = {**body, "command_id": uuid4().hex}
            endpoint, payload, expected = path+"/commands", retry, 202
            if body["command_type"] == "APPROVE_ROUTE":
                endpoint, payload, expected = path+"/route-approvals", {**seed["route_approval"], "command_id": retry["command_id"]}, 200
            result = request(client, "POST", endpoint, expected=expected, json=payload)
            prior = next(r for r in after["receipts"] if r["command_id"] == body["command_id"])
            assert all(result[k] == v for k,v in prior.items())
        assert request(client, "GET", path+"/snapshot") == public
        assert durable_state(database) == after

        # Check continued physical execution with the normal 50 ms scheduler.
        assert request(client, "POST", path+"/commands", expected=202, json=command(public, "RESUME"))["status"] == "APPLIED"
        deadline = monotonic()+5
        while True:
            progressed = request(client, "GET", path+"/snapshot")
            if progressed["tick"] >= public["tick"]+4:
                break
            if monotonic() >= deadline:
                raise TimeoutError("Recovered actor did not resume")
            sleep(.02)
        assert progressed["vehicles"][1]["position"] != public["vehicles"][1]["position"]
        if public["vehicles"][4]["service_state"] == "CHARGING":
            assert progressed["vehicles"][4]["battery_kwh"] > public["vehicles"][4]["battery_kwh"]
        else:
            assert public["vehicles"][0]["service_state"] == "AWAITING_GUIDANCE"
            assert progressed["vehicles"][0]["position"] == public["vehicles"][0]["position"]
        assert request(client, "POST", path+"/commands", expected=202, json=command(progressed, "PAUSE"))["status"] == "APPLIED"
        write(directory/"resumed.json", request(client, "GET", path+"/snapshot"))
        final_trace = request(client, "GET", "/_crash_probe/state")["trace"]
        records = final_trace.pop("records")
        (second.output/"raw-timings.jsonl").write_text("".join(json.dumps(r, sort_keys=True)+"\n" for r in records), encoding="utf-8")
        write(second.output/"trace-metadata.json", final_trace)
        assert final_trace["dropped_records"] == 0
        result = {"status": "PASS", "fault": fault, "session_id": public["session_id"],
            "before_epoch": before["snapshot"]["session_epoch"], "recovered_epoch": public["session_epoch"],
            "durable_tick": public["tick"], "crash": crash, "restart_started_ns": second.started_ns,
            "first_snapshot_ns": snapshot_ns, "verified_ns": verified_ns,
            "crash_to_verified_paused_ms": (verified_ns-crash["requested_ns"])/1e6,
            "restart_to_verified_paused_ms": (verified_ns-second.started_ns)/1e6,
            "command_count": len(after["commands"]), "events_before": len(before["events"]),
            "events_after": len(after["events"]), "pending_reconfirmed": sum(r["status"] == "NEEDS_RECONFIRMATION" for r in after["receipts"]),
            "checks": ["sqlite_integrity", "paused_new_epoch", "durable_boundary", "private_execution",
                "receipt_survival", "ordered_public_replay", "paused_stability", "retry_no_duplicate_effect", "resume_physics",
                "single_trace_header", "recovery_trace_retained"]}
        write(directory/"summary.json", result)
        return result
    finally:
        for process in processes:
            process.close()
        if pending_thread:
            pending_thread.join(timeout=5)
            write(directory/"interrupted-request.json", transport)


def run(output, *, repeats=3, cases=None):
    """Run bounded isolated crash trials; keep source and partial failure evidence."""
    cases = tuple(CASES) if cases is None else tuple(cases)
    if type(repeats) is not int or not 1 <= repeats <= 10 or not cases or len(set(cases)) != len(cases) or any(c not in CASES for c in cases):
        raise ValueError("Invalid crash trial counts or cases")
    output = Path(output)
    if output.exists() and any(output.iterdir()):
        raise ValueError("Output directory must be new or empty")
    output.mkdir(parents=True, exist_ok=True)
    from src.services.training_benchmark import source_metadata, summary
    metadata = source_metadata()
    write(output/"manifest.json", {**metadata, "recorded_at_utc": datetime.now(UTC).isoformat(),
        "profile": "P0_PROCESS_CRASH", "repeats": repeats, "cases": cases,
        "preparation_clock": "MANUAL_TEST_NOT_MEASURED", "recovery_clock": "perf_counter_ns",
        "target_p95_ms": 5000, "client_auth": "EPHEMERAL_MEMORY_ACCOUNT_WITH_PRODUCTION_MIDDLEWARE"})
    trials = []
    try:
        seeds = prepare(output, {CASES[c][0] for c in cases})
        for repeat in range(repeats):
            for case in cases if repeat % 2 == 0 else reversed(cases):
                directory = output/f"{repeat+1:02d}-{case}"
                measured = trial(directory, seeds[CASES[case][0]], CASES[case][1])
                measured.update(case=case, repeat=repeat+1)
                trials.append(measured)
                print(json.dumps({"case": case, "repeat": repeat+1,
                    "crash_to_verified_paused_ms": measured["crash_to_verified_paused_ms"]}), flush=True)
        timings = summary([t["crash_to_verified_paused_ms"] for t in trials])
        result = {"artifact_version": 1, "task": "BE-M2-06", "correctness_status": "PASS",
            "acceptance_status": "NOT_ASSESSED_P1", "profile": "P0_PROCESS_CRASH", "trials": trials,
            "crash_to_verified_paused": timings,
            "restart_to_verified_paused": summary([t["restart_to_verified_paused_ms"] for t in trials]),
            "by_case": {case: summary([t["crash_to_verified_paused_ms"] for t in trials if t["case"] == case]) for case in cases},
            "reference_comparison": "WITHIN_REFERENCE" if timings["p95_ms"] <= 5000 else "ABOVE_REFERENCE",
            "limitations": ["Small P0 sample on unlocked hardware; no P1 claim", "Accelerated fixture setup is excluded",
                "Forced process termination only; no power loss, disk fault, simultaneous clients or reconnect acceptance",
                "Interrupted POST has no application acknowledgement; its durable receipt is queried before the crash"]}
        write(output/"summary.json", result)
        return result
    except BaseException as error:
        write(output/"failure.json", {"correctness_status": "FAIL", "error_code": type(error).__name__, "trials": trials})
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--serve", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--database", type=Path, help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.serve:
        serve(args.database, args.output)
    else:
        result = run(args.output, repeats=args.repeats)
        print(json.dumps(result["crash_to_verified_paused"], indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
