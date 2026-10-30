"""
Parses KITTI OXTS (GPS/IMU) frame files into structured Trajectory data.

Author: Choo Jian Wei
Date Created: 14/09/2026
Last Modified: 15/09/2026

Description:
    This module reads KITTI oxts/data/*.txt files -- one line of 30
    whitespace-separated values per frame -- and parses them into
    OxtsRecord/Trajectory objects (see trajectory_models.py).

    Floating-point sensor fields are parsed as floats and status/mode
    fields are parsed as integers according to the KITTI OXTS data
    format. Floating-point values must be finite; NaN and infinity
    values are rejected.

    No coordinate conversion, timestamp processing, or trajectory
    normalization happens here. This module is responsible only for
    validating and parsing the raw OXTS data. Coordinate conversion
    and canonical trajectory generation belong to the downstream
    normalization stage.
"""


from __future__ import annotations

import os
import math
from pathlib import Path
from typing import TypeAlias

from .trajectory_models import OxtsRecord, Trajectory

PathLike: TypeAlias = str | os.PathLike[str]
# Integer-valued fields defined by the KITTI OXTS format.
# These fields must contain integer tokens rather than arbitrary
# floating-point values.
_OXTS_INTEGER_FIELDS = {
    "navstat",
    "numsats",
    "posmode",
    "velmode",
    "orimode",
}
# Field order per KITTI's oxts/dataformat.txt. This list IS the contract
# between a raw line of 30 numbers and a named OxtsRecord -- do not reorder
# without re-checking against the actual spec in a downloaded drive.
_OXTS_FIELD_ORDER: tuple[str, ...]=(
    "lat","lon","alt","roll","pitch","yaw","vn","ve","vf","vl","vu",
    "ax","ay","az","af","al","au","wx","wy","wz","wf","wl","wu",
    "pos_accuracy","vel_accuracy","navstat","numsats","posmode",
    "velmode","orimode"
)
"""
# -----------------------------------------------------------------------------------
# Class: KittiParseError
# -----------------------------------------------------------------------------------
Description:
    This exception is raised when a KITTI OXTS file cannot be parsed --
    because it doesn't exist, isn't readable, doesn't contain the expected
    number of whitespace-separated fields, contains an invalid numeric
    value, uses a non-integer value for an integer field, or contains a
    non-finite floating-point value.

    It is a plain ValueError subclass, not a core.errors.AppError subclass,
    so this module stays framework-agnostic (same pattern as
    routing/graph_loader.py: domain modules raise plain exceptions, and only
    the API layer translates them into HTTP errors).

"""
class KittiParseError(ValueError):
    """Parse error for KITTI OXTS files."""

"""
# -----------------------------------------------------------------------------------
# Function: _to_path
# -----------------------------------------------------------------------------------
Description:
    This function converts a given path-like value to a Path object,
    raising a KittiParseError if the value is not a valid filesystem path.

Args:
    path_value (PathLike): The path-like value to convert.

Returns:
    Path: The converted Path object.

Raises:
    KittiParseError: If the value is not a valid filesystem path.

"""
def _to_path(path_value: PathLike) -> Path:
    try:
        raw_path = os.fspath(path_value)
        if not isinstance(raw_path, str) or not raw_path.strip():
            raise ValueError
        return Path(raw_path).expanduser()
    except (TypeError, ValueError, OSError) as error:
        raise KittiParseError(f"Not a valid filesystem path: {path_value!r}") from error


"""
# -----------------------------------------------------------------------------------
# Function: parse_oxts_frame
# -----------------------------------------------------------------------------------
Description:
    This function parses a single KITTI oxts/data/NNNNNNNNNN.txt file
    into an OxtsRecord.

    Each frame contains exactly 30 whitespace-separated values in the
    field order defined by KITTI's oxts/dataformat.txt. Floating-point
    sensor values are parsed as float, while navstat, numsats, posmode,
    velmode, and orimode are parsed as int.

    Floating-point values must be finite. Values such as NaN and
    positive/negative infinity are rejected because they do not
    represent valid sensor measurements.

Args:
    path (PathLike): Path to a single OXTS frame file.

Returns:
    OxtsRecord: The validated and parsed OXTS reading.

Raises:
    KittiParseError: If the file cannot be read, is empty, does not
        contain exactly 30 fields, contains an invalid numeric value,
        uses a non-integer value for an integer field, or contains a
        non-finite floating-point value.
"""

def parse_oxts_frame(path: PathLike) -> OxtsRecord:
    frame_path = _to_path(path)

    try:
        raw_text = frame_path.read_text().strip()
    except OSError as error:
        raise KittiParseError(f"Could not read OXTS frame {frame_path}: {error}") from error

    if not raw_text:
        raise KittiParseError(f"OXTS frame {frame_path} is empty")

    raw_fields = raw_text.split()
    if len(raw_fields) != len(_OXTS_FIELD_ORDER):
        raise KittiParseError(
            f"{frame_path}: expected {len(_OXTS_FIELD_ORDER)} fields, "
            f"got {len(raw_fields)}"
        )

    values: list[float | int] = []
    for field_name, raw_field in zip(_OXTS_FIELD_ORDER, raw_fields):
        try:
            if field_name in _OXTS_INTEGER_FIELDS:
                integer_value = float(raw_field)
                if not math.isfinite(integer_value) or not integer_value.is_integer():
                    raise ValueError(f"{field_name} must be a finite integer")
                values.append(int(integer_value))
            else:
                value = float(raw_field)
                if not math.isfinite(value):
                    raise ValueError(f"{field_name} must be finite")
                values.append(value)
        except ValueError as error:
            raise KittiParseError(
                f"{frame_path}: non-numeric field {field_name}={raw_field!r}"
            ) from error

    return OxtsRecord(**dict(zip(_OXTS_FIELD_ORDER, values)))

"""
# -----------------------------------------------------------------------------------
# Function: load_trajectory
# -----------------------------------------------------------------------------------
Description:
    This function loads every OXTS frame in a KITTI sequence's oxts/data/
    directory and returns them as a single ordered Trajectory. Frames are
    sorted by filename before parsing, since KITTI frame filenames are
    zero-padded frame indices (e.g. 0000000000.txt, 0000000001.txt, ...)
    and raw directory listing order is not guaranteed to match that
    sequence.

Args:
    sequence_dir (PathLike): Path to a KITTI drive's oxts/data/ directory
        (the oxts/data subdirectory itself, not the drive root).
    sequence_id (str | None): Identifier stored on the returned Trajectory
        for traceability back to the source drive. Defaults to the name of
        sequence_dir's grandparent directory (i.e. the drive folder name,
        assuming the standard <drive>/oxts/data layout) if not given.

Returns:
    Trajectory: The parsed, ordered sequence of OxtsRecord frames.

Raises:
    KittiParseError: If sequence_dir does not exist or is not a directory,
        contains no .txt frame files, or if any individual frame fails to
        parse (see parse_oxts_frame).

"""
def load_trajectory(sequence_dir: PathLike, sequence_id: str | None = None) -> Trajectory:
    directory = _to_path(sequence_dir)

    if not directory.is_dir():
        raise KittiParseError(f"OXTS data directory does not exist: {directory}")

    frame_paths = sorted(directory.glob("*.txt"))
    if not frame_paths:
        raise KittiParseError(f"No .txt OXTS frames found in {directory}")

    records = [parse_oxts_frame(frame_path) for frame_path in frame_paths]

    resolved_sequence_id = sequence_id or directory.parent.parent.name
    return Trajectory(sequence_id=resolved_sequence_id, records=records)
