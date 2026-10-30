"""
Unit tests for kitti_loader.py.

Author: Choo Jian Wei
Date: 14/09/2026
Last Modified: 14/09/2026

Description:
    Tests for parse_oxts_frame and load_trajectory against small, checked-in
    fixture files (see fixtures/sample_oxts/). Unlike
    test_graph_loader_integration.py, these are pure/offline tests -- OXTS
    parsing has no network dependency, so no conftest.py setup is needed
    and there's no "integration" vs "unit" split for this module.

"""

from pathlib import Path

import pytest

from backend.src.datasets.kitti_loader import (
    KittiParseError,
    load_trajectory,
    parse_oxts_frame,
)

FIXTURES_DIR = Path(__file__).parent / "fixtures" / "sample_oxts"

"""
# -----------------------------------------------------------------------------------
# Function: test_parse_oxts_frame_returns_expected_fields
# -----------------------------------------------------------------------------------
Description:
    Parses a known fixture frame and confirms specific field values match
    what's actually in the file, catching field-order mistakes (e.g. lat/lon
    swapped) that a "did it not crash" test alone would miss.

"""
def test_parse_oxts_frame_returns_expected_fields():
    record = parse_oxts_frame(FIXTURES_DIR / "0000000000.txt")
    assert record.lat == pytest.approx(49.015003)
    assert record.lon == pytest.approx(8.4342971002335)
    assert record.numsats == pytest.approx(11)

"""
# -----------------------------------------------------------------------------------
# Function: test_parse_oxts_frame_rejects_malformed_line
# -----------------------------------------------------------------------------------
Description:
    Truncates a real fixture line to fewer than 30 fields and confirms a
    KittiParseError is raised, naming the expected field count, rather than
    a raw IndexError/TypeError leaking out of the dataclass constructor.

"""
def test_parse_oxts_frame_rejects_malformed_line(tmp_path):
    original_fields = (FIXTURES_DIR / "0000000000.txt").read_text().split()
    bad_frame = tmp_path / "truncated.txt"
    bad_frame.write_text(" ".join(original_fields[:10]))

    with pytest.raises(KittiParseError, match="expected 30 fields"):
        parse_oxts_frame(bad_frame)

"""
# -----------------------------------------------------------------------------------
# Function: test_parse_oxts_frame_rejects_non_numeric_field
# -----------------------------------------------------------------------------------
Description:
    Corrupts one field of a real fixture line with non-numeric text and
    confirms a KittiParseError is raised, rather than a raw ValueError from
    float() leaking out.

"""
def test_parse_oxts_frame_rejects_non_numeric_field(tmp_path):
    original_fields = (FIXTURES_DIR / "0000000000.txt").read_text().split()
    original_fields[0] = "not-a-number"
    bad_frame = tmp_path / "corrupted.txt"
    bad_frame.write_text(" ".join(original_fields))

    with pytest.raises(KittiParseError, match="non-numeric field"):
        parse_oxts_frame(bad_frame)


"""
# -----------------------------------------------------------------------------------
# Function: test_load_trajectory_returns_frames_in_order
# -----------------------------------------------------------------------------------
Description:
    Loads all OXTS frames in the fixture directory and confirms they are
    returned in filename-sorted (chronological) order. The test does not
    depend on specific latitude values, so it works with different fixture
    files.
"""
def test_load_trajectory_returns_frames_in_order():
    frame_files = sorted(FIXTURES_DIR.glob("*.txt"))

    trajectory = load_trajectory(FIXTURES_DIR, sequence_id="test_sequence")

    assert trajectory.sequence_id == "test_sequence"
    assert len(trajectory) == len(frame_files)

    expected_records = [
        parse_oxts_frame(frame_file)
        for frame_file in frame_files
    ]

    assert trajectory.records == expected_records

"""
# -----------------------------------------------------------------------------------
# Function: test_load_trajectory_default_sequence_id
# -----------------------------------------------------------------------------------
Description:
    Confirms that when sequence_id is not given, load_trajectory falls back
    to the drive folder name (grandparent of the oxts/data directory),
    matching KITTI's standard <drive>/oxts/data/ layout.

"""
def test_load_trajectory_default_sequence_id(tmp_path):
    drive_dir = tmp_path / "2011_09_26_drive_0001_sync" / "oxts" / "data"
    drive_dir.mkdir(parents=True)
    (drive_dir / "0000000000.txt").write_text(
        (FIXTURES_DIR / "0000000000.txt").read_text()
    )

    trajectory = load_trajectory(drive_dir)
    assert trajectory.sequence_id == "2011_09_26_drive_0001_sync"

"""
# -----------------------------------------------------------------------------------
# Function: test_load_trajectory_empty_directory_raises
# -----------------------------------------------------------------------------------
Description:
    Confirms that an existing but empty oxts/data directory raises a
    KittiParseError rather than silently returning an empty Trajectory --
    an empty sequence almost always indicates a data problem upstream, not
    a valid state to silently accept.

"""
def test_load_trajectory_empty_directory_raises(tmp_path):
    empty_dir = tmp_path / "empty_oxts"
    empty_dir.mkdir()

    with pytest.raises(KittiParseError, match="No .txt OXTS frames found"):
        load_trajectory(empty_dir)

"""
# -----------------------------------------------------------------------------------
# Function: test_load_trajectory_nonexistent_directory_raises
# -----------------------------------------------------------------------------------
Description:
    Confirms a missing directory raises a clean KittiParseError rather than
    a raw FileNotFoundError.

"""
def test_load_trajectory_nonexistent_directory_raises(tmp_path):
    with pytest.raises(KittiParseError, match="does not exist"):
        load_trajectory(tmp_path / "does_not_exist")
"""
# -----------------------------------------------------------------------------------
# Function: test_parse_oxts_frame_preserves_integer_field_types
# -----------------------------------------------------------------------------------
Description:
    Confirms that the five status/count fields (navstat, numsats, posmode,
    velmode, orimode) come back as Python int on OxtsRecord, not float --
    these are documented as integer codes/counts in KITTI's dataformat.txt,
    even though every field is parsed from the same whitespace-separated
    line of numbers.

"""
def test_parse_oxts_frame_preserves_integer_field_types(tmp_path):
    frame_path = tmp_path / "0000000000.txt"
    values = ["0"] * 30

    values[25] = "4"   # navstat
    values[26] = "10"  # numsats
    values[27] = "1"   # posmode
    values[28] = "2"   # velmode
    values[29] = "3"   # orimode

    frame_path.write_text(" ".join(values))

    record = parse_oxts_frame(frame_path)

    assert isinstance(record.navstat, int)
    assert isinstance(record.numsats, int)
    assert isinstance(record.posmode, int)
    assert isinstance(record.velmode, int)
    assert isinstance(record.orimode, int)

"""
# -----------------------------------------------------------------------------------
# Function: test_parse_oxts_frame_rejects_non_integer_status_field
# -----------------------------------------------------------------------------------
Description:
    Confirms that a fractional value (e.g. "4.5") in one of the integer
    status/count fields raises a KittiParseError, rather than silently
    truncating or coercing it to an int -- a fractional navstat/numsats/
    posmode/velmode/orimode value indicates a malformed or corrupted frame,
    not a valid reading.

"""
def test_parse_oxts_frame_rejects_non_integer_status_field(tmp_path):
    frame_path = tmp_path / "0000000000.txt"
    values = ["0"] * 30
    values[25] = "4.5"

    frame_path.write_text(" ".join(values))

    with pytest.raises(KittiParseError):
        parse_oxts_frame(frame_path)


@pytest.mark.parametrize("invalid_value", ["nan", "inf", "-inf"])
def test_parse_oxts_frame_rejects_non_finite_integer_status_field(
    tmp_path, invalid_value
):
    frame_path = tmp_path / "0000000000.txt"
    values = ["0"] * 30
    values[25] = invalid_value
    frame_path.write_text(" ".join(values))

    with pytest.raises(KittiParseError):
        parse_oxts_frame(frame_path)


def test_parse_oxts_frame_accepts_integral_scientific_status_field(tmp_path):
    frame_path = tmp_path / "0000000000.txt"
    values = ["0"] * 30
    values[25] = "4.0e0"
    frame_path.write_text(" ".join(values))

    assert parse_oxts_frame(frame_path).navstat == 4
"""
# -----------------------------------------------------------------------------------
# Function: test_parse_oxts_frame_rejects_non_finite_value
# -----------------------------------------------------------------------------------
Description:
    Confirms that a non-finite value ("nan") in a field raises a
    KittiParseError rather than being accepted -- float("nan") parses
    successfully in Python, so this guards against silently propagating a
    NaN into an OxtsRecord, where it would corrupt any downstream
    computation (e.g. the coordinate/speed calculations in
    trajectory_normalizer.py) without an obvious error at the source.

"""
def test_parse_oxts_frame_rejects_non_finite_value(tmp_path):
    frame_path = tmp_path / "0000000000.txt"
    values = ["0"] * 30
    values[0] = "nan"

    frame_path.write_text(" ".join(values))

    with pytest.raises(KittiParseError):
        parse_oxts_frame(frame_path)
