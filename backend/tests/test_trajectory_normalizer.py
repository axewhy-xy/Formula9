"""
Unit tests for trajectory_normalizer.py.

Author: Choo Jian Wei
Date Created: 14/09/2026
Last Modified: 14/09/2026

Description:
    Tests for normalize_trajectory and nearest_sample against the same
    small, checked-in fixture files used by test_kitti_loader.py (see
    fixtures/sample_oxts/ and fixtures/timestamps.txt). Pure/offline tests.

    fixtures/sample_oxts/ deliberately has a gap (frame 3's file is
    missing, frames 0/1/2/4/5 exist). test_normalize_trajectory_handles_
    frame_gap specifically checks that this gap does not misalign
    timestamps for frames after it.
"""
from __future__ import annotations
from pathlib import Path
import pytest

from backend.src.datasets.kitti_loader import KittiParseError, load_trajectory
from backend.src.datasets.trajectory_normalizer import nearest_sample, normalize_trajectory

FIXTURES_DIR = Path(__file__).parent / "fixtures" / "sample_oxts"

# -----------------------------------------------------------------------------------
# Function: test_normalize_trajectory_first_frame_is_origin
# -----------------------------------------------------------------------------------
"""
Description:
    This function verifies that the first normalized trajectory sample is
    used as the local coordinate origin.

    The first sample should have local position (0, 0, 0), simulation time
    of 0.0 seconds, and source frame index 0.

Arguments:
    None.

Expected Result:
    The first sample has x, y, and z positions of 0.0, a simulation time
    of 0.0, and a source frame index of 0.
"""
def test_normalize_trajectory_first_frame_is_origin():
    """The first normalized sample should be used as the local trajectory origin."""
    trajectory = load_trajectory(FIXTURES_DIR, sequence_id="test_sequence")
    samples = normalize_trajectory(trajectory, FIXTURES_DIR)
    first = samples[0]
    assert first.position.x == pytest.approx(0.0)
    assert first.position.y == pytest.approx(0.0)
    assert first.position.z == pytest.approx(0.0)
    assert first.simulation_time == pytest.approx(0.0)
    assert first.source_frame == 0

# -----------------------------------------------------------------------------------
# Function: test_normalize_trajectory_handles_frame_gap
# -----------------------------------------------------------------------------------
"""
Description:
    This function verifies that missing source frame files do not cause
    timestamps to become misaligned with their original frame indices.

    The fixture intentionally contains a gap in its source frame files.
    Frame indices are expected to be derived from the filenames rather than
    from the position of each record in the parsed trajectory.

Arguments:
    None.

Expected Result:
    Each normalized sample retains its correct source frame index and frame
    4 receives the timestamp associated with timestamp entry 4.
"""
def test_normalize_trajectory_handles_frame_gap():
    """Frame 4 must get timestamps.txt line 4, not line 3 (what a naive
    positional zip would incorrectly use, given frame 3's file is missing)."""
    trajectory = load_trajectory(FIXTURES_DIR, sequence_id="test_sequence")
    samples = normalize_trajectory(trajectory, FIXTURES_DIR)
    source_frames = [sample.source_frame for sample in samples]
    assert source_frames == [0, 1, 2, 3, 4, 5], "fixture gap at frame 3 changed unexpectedly"
    frame_4_sample = next(s for s in samples if s.source_frame == 4)
    assert frame_4_sample.simulation_time == pytest.approx(0.400815, abs=1e-4)
# -----------------------------------------------------------------------------------
# Function: test_normalize_trajectory_orientation_in_degrees
# -----------------------------------------------------------------------------------
"""
Description:
    This function verifies that the pitch and roll values in the normalized
    orientation are converted from radians into degrees.

Arguments:
    None.

Expected Result:
    The normalized pitch and roll values match the corresponding source
    OXTS values after conversion to degrees.
"""
def test_normalize_trajectory_orientation_in_degrees():
    """Orientation fields must be converted to degrees, not left as raw radians."""
    trajectory = load_trajectory(FIXTURES_DIR, sequence_id="test_sequence")
    samples = normalize_trajectory(trajectory, FIXTURES_DIR)
    first_record = trajectory.records[0]
    first_sample = samples[0]
    assert first_sample.orientation.pitch == pytest.approx(__import__("math").degrees(first_record.pitch))
    assert first_sample.orientation.roll == pytest.approx(__import__("math").degrees(first_record.roll))

# -----------------------------------------------------------------------------------
# Function: test_normalize_trajectory_frame_count_mismatch_raises
# -----------------------------------------------------------------------------------
"""
Description:
    This function verifies that normalization rejects a Trajectory when
    the number of source frame files does not match the number of parsed
    trajectory records.

    A mismatch may indicate that the Trajectory and OXTS data directory
    originated from different sources.

Arguments:
    tmp_path: Pytest temporary directory used to create an empty OXTS
        directory and timestamps file.

Expected Result:
    normalize_trajectory raises KittiParseError when the frame counts
    do not match.
"""
def test_normalize_trajectory_frame_count_mismatch_raises(tmp_path):
    """A Trajectory built from one directory can't be silently normalized against a mismatched one."""
    trajectory = load_trajectory(FIXTURES_DIR, sequence_id="test_sequence")
    empty_dir = tmp_path / "oxts" / "data"
    empty_dir.mkdir(parents=True)
    (tmp_path / "oxts" / "timestamps.txt").write_text("")
    with pytest.raises(KittiParseError, match="does not match"):
        normalize_trajectory(trajectory, empty_dir)
# -----------------------------------------------------------------------------------
# Function: test_nearest_sample_returns_closest_by_simulation_time
# -----------------------------------------------------------------------------------
"""
Description:
    This function verifies that nearest_sample returns the normalized
    trajectory sample with the simulation time closest to the requested
    time.

Arguments:
    None.

Expected Result:
    A request for 0.25 seconds returns frame 2, whose simulation time is
    approximately 0.2 seconds, rather than frame 4 at approximately
    0.4 seconds.
"""
def test_nearest_sample_returns_closest_by_simulation_time():
    """A request at 0.25s should land on frame 2 (~0.2s), not frame 4 (~0.4s)."""
    trajectory = load_trajectory(FIXTURES_DIR, sequence_id="test_sequence")
    samples = normalize_trajectory(trajectory, FIXTURES_DIR)
    result = nearest_sample(samples, 0.25)
    assert result.source_frame == 2
# -----------------------------------------------------------------------------------
# Function: test_nearest_sample_empty_list_raises
# -----------------------------------------------------------------------------------
"""
Description:
    This function verifies that nearest_sample rejects an empty list of
    trajectory samples.

Arguments:
    None.

Expected Result:
    nearest_sample raises ValueError when no samples are available for
    lookup.
"""
def test_nearest_sample_empty_list_raises():
    with pytest.raises(ValueError):
        nearest_sample([], 1.0)