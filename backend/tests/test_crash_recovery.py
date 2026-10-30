"""
Process-crash durability and recovery evidence regressions for BE-M2-06.

Author: Nur Syazliyarni Binte Zazali
Date Created: 28/10/2026
Last Modified: 28/10/2026

Description:
    Runs real isolated HTTP backends through four forced termination points.
    Verifies transaction rollback, receipt reconciliation, private hold/charge
    execution, nonduplicating retries, evidence retention and helper cleanup.
    Recovery timings are observed without asserting a machine-specific budget.
"""

from copy import deepcopy
import json
from types import SimpleNamespace

import pytest

from src.services import crash_recovery


@pytest.fixture(scope="module")
def drill(tmp_path_factory):
    output = tmp_path_factory.mktemp("crash-recovery")/"evidence"
    result = crash_recovery.run(output, repeats=1)
    return output, result


def test_real_process_crashes_restore_durable_workflow_and_reconcile_commands(drill):
    output, result = drill
    assert result["correctness_status"] == "PASS" and result["acceptance_status"] == "NOT_ASSESSED_P1"
    assert len(result["trials"]) == 4 and result["crash_to_verified_paused"]["samples"] == 4
    assert set(result["by_case"]) == set(crash_recovery.CASES)
    for trial in result["trials"]:
        directory = output/f"01-{trial['case']}"
        assert trial["crash"]["exit_code"] != 0
        assert 0 < trial["crash_to_verified_paused_ms"] < 30000
        assert trial["recovered_epoch"] == trial["before_epoch"]+1
        after = json.loads((directory/"after.json").read_bytes())
        gate = json.loads((directory/"before-crash/fault-reached.json").read_bytes())
        assert gate["pid"] == trial["crash"]["pid"]
        if trial["fault"] == "UNCOMMITTED_TICK":
            assert gate["transaction_open"] and gate["attempted_tick"] > trial["durable_tick"]
        else:
            interrupted = json.loads((directory/"interrupted-request.json").read_bytes())
            assert "http_status" not in interrupted and "error_code" in interrupted
        assert trial["pending_reconfirmed"] == int(trial["fault"] == "BEFORE_APPLY")
        for process in ("before-crash", "after-crash"):
            metadata = json.loads((directory/process/"trace-metadata.json").read_bytes())
            raw = (directory/process/"raw-timings.jsonl").read_text().splitlines()
            assert len(raw) == metadata["total_records"] and metadata["dropped_records"] == 0
            cleanup = json.loads((directory/process/"cleanup.json").read_bytes())
            assert cleanup["exited"] and cleanup["exit_code"] is not None
            if process == "after-crash":
                assert cleanup["exit_code"] == 0
        assert after["live_runtime"]["is_paused"]


@pytest.mark.parametrize("damage", ["lost_receipt", "duplicate_event", "private_timer", "public_route"])
def test_recovery_verifier_rejects_durability_and_execution_regressions(drill, damage):
    output, _ = drill
    directory = output/"01-charging_uncommitted_tick"
    before = json.loads((directory/"before.json").read_bytes())
    after = json.loads((directory/"after.json").read_bytes())
    private = deepcopy(after.pop("live_runtime"))
    if damage == "lost_receipt":
        after["receipts"].pop()
    elif damage == "duplicate_event":
        after["events"].append(after["events"][-1])
    elif damage == "private_timer":
        private["runtime"]["workflow"]["charge_elapsed"]["4"] += 1
    else:
        after["snapshot"]["routes"][0]["revision"] += 1
    with pytest.raises((AssertionError, ValueError)):
        crash_recovery.verify(before, after, private)


def test_failed_trial_retains_evidence_and_never_writes_success(tmp_path, monkeypatch):
    output = tmp_path/"failed"
    monkeypatch.setattr(crash_recovery, "prepare", lambda *_: {"HOLD": {}})
    def fail(directory, *_):
        directory.mkdir()
        crash_recovery.write(directory/"observed.json", {"crash_point_reached": True})
        raise RuntimeError("exception text must not leak into the failure artifact")
    monkeypatch.setattr(crash_recovery, "trial", fail)
    with pytest.raises(RuntimeError):
        crash_recovery.run(output, repeats=1, cases=("hold_uncommitted_tick",))
    failure = json.loads((output/"failure.json").read_bytes())
    assert failure == {"correctness_status": "FAIL", "error_code": "RuntimeError", "trials": []}
    assert (output/"01-hold_uncommitted_tick/observed.json").exists()
    assert not (output/"summary.json").exists()


def test_existing_evidence_and_invalid_trial_options_are_rejected(tmp_path):
    output = tmp_path/"existing"
    output.mkdir()
    (output/"keep.txt").write_text("existing evidence")
    with pytest.raises(ValueError, match="new or empty"):
        crash_recovery.run(output)
    for options in ({"repeats": True}, {"repeats": 0}, {"cases": []}, {"cases": ["unknown"]}):
        with pytest.raises(ValueError, match="Invalid"):
            crash_recovery.run(tmp_path/"invalid", **options)
    assert (output/"keep.txt").read_text() == "existing evidence"


def test_marker_polling_tolerates_a_temporary_windows_sharing_lock(monkeypatch):
    values = iter((FileNotFoundError(), PermissionError(), b'{"pid": 123}'))
    def read():
        value = next(values)
        if isinstance(value, Exception):
            raise value
        return value
    monkeypatch.setattr(crash_recovery, "sleep", lambda _: None)
    process = SimpleNamespace(poll=lambda: None)
    assert crash_recovery.wait_signal(SimpleNamespace(read_bytes=read), process,
        crash_recovery.monotonic()+1) == {"pid": 123}


def test_unreadable_marker_has_a_bounded_deadline_and_preserves_the_error(monkeypatch):
    def denied():
        raise PermissionError("inaccessible marker")
    times = iter((0, 2))
    monkeypatch.setattr(crash_recovery, "monotonic", lambda: next(times))
    monkeypatch.setattr(crash_recovery, "sleep", lambda _: None)
    with pytest.raises(TimeoutError) as error:
        crash_recovery.wait_signal(SimpleNamespace(read_bytes=denied), SimpleNamespace(poll=lambda: None), 1)
    assert isinstance(error.value.__cause__, PermissionError)
