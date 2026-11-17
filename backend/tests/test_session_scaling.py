"""
Tests for the multi-session scaling baseline.

Author: Lau Xiang Yang
Date Created: 17/11/2026
Last Modified: 17/11/2026

Description:
    Checks the window analysis (per-session real-time factor, lateness, span shares, request costs), the
    breakpoint and A/B summaries on hand-made records, and one short live run that creates, starts and
    measures a real integrated session with a polling client, leaving the admission caps as it found them.

"""

import json

import pytest

from src.services import session_scaling
from src.services.integrated_actor import IntegratedSessionActor
from src.services.session_scaling import analyse, analyse_client, breakpoints, compare, run, run_case, stats
from src.services.training_sessions import TrainingSessionService

MS = 1_000_000


def progress(session_id, tick, end_ms, epoch=1):
    end = end_ms * MS
    return {"name": "runtime.progress", "session_id": session_id, "session_epoch": epoch, "tick": tick,
            "start_ns": end, "end_ns": end, "outcome": "OK", "duration_ms": 0}


def schedule(session_id, at_ms, lateness, missed=0, running=True):
    return {"name": "tick.schedule", "session_id": session_id, "running": running, "start_ns": at_ms * MS,
            "end_ns": at_ms * MS, "lateness_ms": lateness, "missed_deadlines": missed, "outcome": "OK", "duration_ms": 0}


def span(name, at_ms, duration_ms, **tags):
    return {"name": name, "start_ns": at_ms * MS, "end_ns": (at_ms + duration_ms) * MS,
            "duration_ms": duration_ms, "outcome": "OK", **tags}


def test_stats_are_nearest_rank_and_keep_the_outlier():
    measured = stats([1, 2, 3, 4, 100])
    assert measured["p50"] == 3 and measured["p95"] == 100 and measured["max"] == 100
    assert measured["samples"] == 5 and measured["mean"] == 22
    assert stats([]) is None


def test_window_analysis_reports_each_sessions_pace_and_the_cost_split():
    # Over a 10 s window "fast" advances 200 ticks (1x) and "slow" 100 (0.5x)
    records = [progress("fast", 10, 900), progress("fast", 210, 10_900),
               progress("slow", 10, 950), progress("slow", 110, 10_950),
               # Outside the window either side: must not count
               progress("fast", 250, 12_000), schedule("fast", 500, 999, missed=9),
               schedule("fast", 2_000, 2), schedule("fast", 3_000, 4), schedule("slow", 2_500, 30, missed=1),
               schedule("slow", 4_000, 999, running=False),
               span("tick.compute", 2_000, 5, session_id="fast"), span("tick.compute", 2_500, 15, session_id="slow"),
               span("snapshot.commit", 2_010, 10, session_id="fast"), span("writer.transaction", 2_012, 8),
               span("tick.compute", 1_000, 999, session_id="other-runtime"),
               span("http.publication", 3_000, 4, method="GET", route="/v2/sessions/{session_id}/snapshot",
                    status_code=200, response_bytes=2048)]
    result = analyse(records, session_ids=["fast", "slow"], start_ns=1_000 * MS, end_ns=11_000 * MS,
                     step_s=0.05, cpu_s=5.0)
    assert result["per_session"]["fast"]["real_time_factor"] == pytest.approx(1.0)
    assert result["per_session"]["slow"]["real_time_factor"] == pytest.approx(0.5)
    assert result["real_time_factor"]["min"] == pytest.approx(0.5)
    assert result["real_time_factor"]["below_reference"] == 1
    assert result["running_ticks"] == 3 and result["missed_deadlines"] == 1
    assert result["lateness_ms"]["max"] == 30 and result["per_session"]["fast"]["lateness_ms"]["max"] == 4
    assert result["spans"]["tick.compute"]["samples"] == 2  # Not the other runtime's
    assert result["spans"]["tick.compute"]["busy_share"] == pytest.approx(0.002)
    assert result["work_ms_per_tick"] == pytest.approx((5 + 15 + 10) / 3)
    assert result["process_cpu_cores"] == pytest.approx(0.5)
    assert result["http"][0]["samples"] == 1 and result["http"][0]["response_kib"]["max"] == 2


def test_a_session_with_no_progress_before_the_window_is_unmeasured_not_at_pace():
    records = [progress("late", 10, 5_000), progress("late", 20, 6_000),
               progress("restarted", 10, 500), progress("restarted", 50, 5_000, epoch=2)]
    result = analyse(records, session_ids=["late", "restarted"], start_ns=1_000 * MS, end_ns=11_000 * MS, step_s=0.05)
    assert result["per_session"]["late"]["real_time_factor"] is None
    assert result["per_session"]["restarted"]["real_time_factor"] is None
    assert result["real_time_factor"]["unmeasured_sessions"] == 2 and result["real_time_factor"]["min"] is None
    with pytest.raises(ValueError, match="empty"):
        analyse(records, session_ids=["late"], start_ns=MS, end_ns=MS, step_s=0.05)


def test_client_view_counts_frames_inside_the_window_only():
    class Load:
        snapshots = [(2_000 * MS, 4.0, 200), (500 * MS, 99.0, 200)]
        traffic = [(2_000 * MS, 90.0, 200, 2)]
        frames = [(500 * MS, 1), *((t * MS, tick) for t, tick in zip(range(1_000, 3_000, 100), range(10, 50, 2)))]
        errors = {}
    result = analyse_client(Load, 1_000 * MS, 3_000 * MS)
    assert result["traffic_frames_per_s"] == pytest.approx(10)
    assert result["traffic_frame_tick_span"] == 38
    assert result["snapshot_latency_ms"]["max"] == 4.0
    assert result["traffic_reply_gap_ms"]["p50"] == pytest.approx(100)


def case(sessions, factor, *, traffic=True, client_load=True, work=5.0):
    return {"sessions": sessions, "traffic": traffic, "client_load": client_load, "work_ms_per_tick": work,
            "real_time_factor": {"min": factor}, "lateness_ms": {"p95": 12.0}}


def test_breakpoints_name_the_first_count_that_falls_behind_by_median_over_rounds():
    cases = [case(1, 1.0), case(2, 0.995), case(4, 0.98), case(4, 0.999), case(4, 0.97), case(8, 0.5),
             case(1, 1.0, traffic=False), case(2, 1.0, traffic=False)]
    rows = {(r["traffic"], r["client_load"]): r for r in breakpoints(cases)}
    assert rows[(True, True)]["first_count_below_reference"] == 4  # median of 0.98, 0.999, 0.97
    assert rows[(True, True)]["largest_count_at_reference"] == 2
    assert rows[(False, True)]["first_count_below_reference"] is None
    assert rows[(False, True)]["largest_count_at_reference"] == 2


def test_ab_comparison_takes_each_sides_median_and_the_work_ratio():
    runs = {"baseline": [{"cases": [case(2, 0.9, work=10.0)]}, {"cases": [case(2, 0.8, work=12.0)]}],
            "candidate": [{"cases": [case(2, 1.0, work=6.0)]}, {"cases": [case(2, 0.99, work=5.0)]}]}
    [row] = compare(runs)
    assert row["baseline"]["work_ms_per_tick"] == 11.0 and row["candidate"]["work_ms_per_tick"] == 5.5
    assert row["work_ratio"] == pytest.approx(0.5) and row["candidate"]["rounds"] == 2
    assert "| 2 | on | on |" in session_scaling.ab_report([row], "HEAD")


@pytest.mark.traffic
def test_short_live_run_measures_a_real_session_and_its_client(tmp_path):
    caps = (TrainingSessionService.MAX_SESSIONS, TrainingSessionService.MAX_TRAFFIC_SESSIONS)
    lines = []
    result = run(tmp_path / "scaling", session_counts=(1,), ticks=20, warmup_s=0.5, timeout_s=30,
                 isolate=False, progress=lines.append)
    [measured] = result["cases"]
    assert measured["case"] == "r1-n01-traffic-client" and measured["traffic_sessions_live"] == 1
    assert measured["window_complete"] and measured["trace"]["dropped_records"] == 0
    assert measured["real_time_factor"]["unmeasured_sessions"] == 0 and measured["running_ticks"] >= 19
    assert {"tick.compute", "snapshot.commit", "traffic.frame"} <= measured["spans"].keys()
    assert measured["client"]["traffic_frames_per_s"] > 0 and not measured["client"]["errors"]
    assert any(r["route"].endswith("/snapshot") for r in measured["http"])
    assert json.loads(lines[0])["case"] == measured["case"]
    assert (tmp_path / "scaling" / "report.md").read_text(encoding="utf-8").startswith("# Session scaling baseline")
    assert json.loads((tmp_path / "scaling" / "manifest.json").read_bytes())["parameters"]["ticks"] == 20
    assert (TrainingSessionService.MAX_SESSIONS, TrainingSessionService.MAX_TRAFFIC_SESSIONS) == caps
    assert IntegratedSessionActor.TRAFFIC
    with pytest.raises(ValueError, match="never overwritten"):
        run(tmp_path / "scaling", session_counts=(1,), ticks=20)


def test_each_case_can_run_in_a_process_of_its_own(tmp_path):
    result = run(tmp_path / "isolated", session_counts=(1,), traffic="off", client="off", ticks=20, warmup_s=0.2,
                 timeout_s=30, progress=lambda line: None)
    [measured] = result["cases"]
    assert result["parameters"]["process_per_case"] and measured["case"] == "r1-n01-notraffic-noclient"
    assert measured["traffic_sessions_live"] == 0 and measured["window_complete"]
    assert measured["real_time_factor"]["unmeasured_sessions"] == 0
    saved = json.loads((tmp_path / "isolated" / measured["case"] / "summary.json").read_bytes())
    assert saved["round"] == 1 and saved["sessions"] == 1


def test_a_failed_case_restores_the_admission_caps_and_traffic_choice(tmp_path, monkeypatch):
    saved = (TrainingSessionService.MAX_SESSIONS, TrainingSessionService.MAX_TRAFFIC_SESSIONS,
             IntegratedSessionActor.TRAFFIC)
    def fail(*args):
        raise RuntimeError("start refused")
    monkeypatch.setattr("src.services.training_benchmark.command", fail)
    with pytest.raises(RuntimeError, match="start refused"):
        run_case(tmp_path / "case", sessions=TrainingSessionService.MAX_TRAFFIC_SESSIONS + 1, traffic=True, client_load=False, ticks=20, warmup_s=0, seed=1,
                 timeout_s=5)
    assert (TrainingSessionService.MAX_SESSIONS, TrainingSessionService.MAX_TRAFFIC_SESSIONS,
            IntegratedSessionActor.TRAFFIC) == saved


@pytest.mark.parametrize("arguments", [{"session_counts": ()}, {"session_counts": (0,)}, {"ticks": 5},
                                       {"traffic": "sometimes"}, {"rounds": 0}])
def test_out_of_bounds_parameters_are_refused_before_anything_is_written(tmp_path, arguments):
    with pytest.raises(ValueError, match="bounds"):
        run(tmp_path / "out", **arguments)
    assert not (tmp_path / "out").exists()
