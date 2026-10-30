"""
Normalizes raw KITTI OXTS trajectory data into the M1 canonical contract shape.

Author: Choo Jian Wei
Date Created: 14/09/2026
Last Modified: 14/09/2026

Description:
    This module sits on top of kitti_loader.py's raw parsing output
    (OxtsRecord/Trajectory) and produces CanonicalSample objects matching
    the M1 backend contract: local x/y/z position, degree-based
    orientation, ISO timestamps, simulation_time, and speed. Nothing in
    kitti_loader.py or trajectory_models.py's OxtsRecord/Trajectory changes
    -- this module only adds a transform stage on top.

    Frame indices are re-derived from each frame's filename (not assumed
    to be 0, 1, 2, ... in order) so a gap in the source sequence (a missing
    frame file) doesn't silently misalign timestamps for every frame after
    the gap.
"""
from __future__ import annotations
import math
import os
from datetime import datetime
from pathlib import Path

from .kitti_loader import KittiParseError, PathLike
from .trajectory_models import CanonicalSample, Orientation, Position, Trajectory

_EARTH_RADIUS_M = 6378137.0  # WGS84 equatorial radius, metres
# Scene-linked samples must match SceneRepository._project and
# MapRenderer::lonLatToLocal exactly, not just share the same origin.
_SCENE_METRES_PER_DEGREE_LATITUDE = 110_574.0
_SCENE_METRES_PER_DEGREE_LONGITUDE_AT_EQUATOR = 111_320.0

# -----------------------------------------------------------------------------------
# Function: _to_path
# -----------------------------------------------------------------------------------
"""
Description:
    This function converts a PathLike value into a pathlib.Path object.
    The resulting path is expanded to resolve the user's home directory.

    Invalid, empty, or unsupported path values are rejected and reported as
    a KittiParseError.

Arguments:
    path_value: Filesystem path value to convert.

Returns:
    A pathlib.Path object representing the supplied filesystem path.

Raises:
    KittiParseError: If the supplied value is not a valid filesystem path.
"""
def _to_path(path_value: PathLike) -> Path:
    try:
        raw_path = os.fspath(path_value)
        if not isinstance(raw_path, str) or not raw_path.strip():
            raise ValueError
        return Path(raw_path).expanduser()
    except (TypeError, ValueError, OSError) as error:
        raise KittiParseError(
            f"Not a valid filesystem path: {path_value!r}"
        ) from error
# -----------------------------------------------------------------------------------
# Function: _frame_index_from_path
# -----------------------------------------------------------------------------------
"""
Description:
    This function extracts the integer frame index from an OXTS frame
    filename. The filename stem is expected to contain a numeric frame
    index, such as "0000000004.txt" producing frame index 4.

Arguments:
    frame_path: Path to the OXTS frame file.

Returns:
    The integer frame index extracted from the filename.

Raises:
    KittiParseError: If the filename does not contain a numeric frame index.
"""

def _frame_index_from_path(frame_path: Path) -> int:
    """Extracts the integer frame index from a filename, e.g. "0000000004.txt" -> 4."""
    try:
        return int(frame_path.stem)
    except ValueError as error:
        raise KittiParseError(f"OXTS frame filename is not a numeric index: {frame_path.name}") from error
# -----------------------------------------------------------------------------------
# Function: _read_timestamps
# -----------------------------------------------------------------------------------
"""
Description:
    This function reads the KITTI oxts/timestamps.txt file and converts
    each valid timestamp line into a Python datetime object.

    Timestamp entries are indexed by their original line position so that
    the timestamp for a source frame remains associated with its original
    frame index, even if a corresponding data file is missing.

    KITTI timestamps contain nanosecond precision. Since Python datetime
    supports microsecond precision, fractional seconds are truncated to
    six digits.

Arguments:
    timestamps_path: Path to the KITTI timestamps.txt file.

Returns:
    A list of datetime objects corresponding to the timestamp entries.

Raises:
    KittiParseError: If the timestamps file cannot be read or a timestamp
        cannot be parsed.
"""

def _read_timestamps(timestamps_path: PathLike) -> list[datetime]:
    """Reads oxts/timestamps.txt, one datetime per line, indexed by line number
    (line 3 = frame 3's timestamp, regardless of whether frame 3's data file exists)."""
    path = _to_path(timestamps_path)
    try:
        lines = path.read_text().splitlines()
    except OSError as error:
        raise KittiParseError(f"Could not read timestamps file: {path}") from error

    timestamps: list[datetime] = []
    for line_number, line in enumerate(lines):
        line = line.strip()
        if not line:
            continue
        # KITTI timestamps have nanosecond precision; Python's datetime only
        # supports microseconds, so truncate the fractional part to 6 digits.
        date_part, _, frac_part = line.partition(".")
        frac_part = (frac_part + "000000")[:6]
        try:
            timestamps.append(datetime.strptime(f"{date_part}.{frac_part}", "%Y-%m-%d %H:%M:%S.%f"))
        except ValueError as error:
            raise KittiParseError(f"{timestamps_path}: could not parse timestamp on line {line_number}: {line!r}") from error
    return timestamps
# -----------------------------------------------------------------------------------
# Function: _latlon_to_local_xy
# -----------------------------------------------------------------------------------
"""
Description:
    This function converts a latitude/longitude coordinate into local
    Cartesian x/y coordinates using a scale-corrected Mercator projection.

    The projection follows the same approach used by KITTI's
    latlonToMercator.m and is calculated relative to the supplied origin
    latitude and longitude.

Arguments:
    lat: Latitude of the point in degrees.
    lon: Longitude of the point in degrees.
    origin_lat: Latitude of the local coordinate origin in degrees.
    origin_lon: Longitude of the local coordinate origin in degrees.
    scale: Latitude-dependent Mercator scale factor.

Returns:
    A tuple containing the local x and y coordinates in metres.
"""
def _latlon_to_local_xy(lat: float, lon: float, origin_lat: float, origin_lon: float, scale: float) -> tuple[float, float]:
    """Scale-corrected Mercator projection (same approach as KITTI's own
    devkit latlonToMercator.m), relative to an origin lat/lon."""
    def mercator(lat_deg: float, lon_deg: float) -> tuple[float, float]:
        mx = scale * math.radians(lon_deg) * _EARTH_RADIUS_M
        my = scale * _EARTH_RADIUS_M * math.log(math.tan((90.0 + lat_deg) * math.pi / 360.0))
        return mx, my
    origin_x, origin_y = mercator(origin_lat, origin_lon)
    point_x, point_y = mercator(lat, lon)
    return point_x - origin_x, point_y - origin_y
def _latlon_to_scene_xy(
    lat: float,
    lon: float,
    origin_lat: float,
    origin_lon: float,
) -> tuple[float, float]:
    """Project GPS into the static scene's local east/north metre frame.

    This intentionally matches SceneRepository._project and the map renderer.
    KITTI's scaled Mercator Y differs from the static map's linear latitude Y;
    using it for scene-linked samples misaligns the ego and static geometry.
    """
    longitude_scale = _SCENE_METRES_PER_DEGREE_LONGITUDE_AT_EQUATOR * math.cos(
        origin_lat * math.pi / 180.0
    )
    return (
        (lon - origin_lon) * longitude_scale,
        (lat - origin_lat) * _SCENE_METRES_PER_DEGREE_LATITUDE,
    )


# -----------------------------------------------------------------------------------
# Function: normalize_trajectory
# -----------------------------------------------------------------------------------
"""
Description:
    This function converts a raw Trajectory into a list of CanonicalSample
    objects matching the project's M1 canonical contract.

    Position is converted from latitude/longitude/altitude into local
    Cartesian x/y/z coordinates. Orientation is converted from radians into
    degrees, timestamps are converted into ISO 8601 format, simulation time
    is calculated relative to the first frame, and vehicle speed is derived
    from the forward and lateral velocity components.

    Frame indices are re-derived from the source filenames rather than
    assuming that records are numbered consecutively. This prevents missing
    source frames from causing timestamp misalignment.

Arguments:
    trajectory: Parsed OXTS trajectory containing the source records.
    oxts_data_dir: Directory containing the source OXTS frame files and
        associated timestamps.txt file.
    coordinate_origin: Optional latitude/longitude pair used as the local
        x/y origin. When omitted, the first trajectory record remains the
        origin. Scene-associated simulations supply the scene origin so
        static geometry and live trajectory positions share one frame and
        the same linear latitude/longitude projection. Standalone playback
        retains the original KITTI scale-corrected Mercator projection.

Returns:
    A list of CanonicalSample objects in source frame order.

Raises:
    KittiParseError: If the frame count does not match the trajectory,
        timestamps cannot be read, or a required timestamp is missing.
"""
def normalize_trajectory(
    trajectory: Trajectory,
    oxts_data_dir: PathLike,
    *,
    coordinate_origin: tuple[float, float] | None = None,
) -> list[CanonicalSample]:
    """Converts a raw Trajectory into canonical samples. Frame indices are
    re-derived from filenames so gaps in the source sequence don't misalign timestamps."""
    data_dir = _to_path(oxts_data_dir)
    frame_paths = sorted(data_dir.glob("*.txt"))
    frame_indices = [_frame_index_from_path(p) for p in frame_paths]

    if len(frame_indices) != len(trajectory.records):
        raise KittiParseError(
            f"{data_dir}: frame count ({len(frame_indices)}) does not match "
            f"trajectory record count ({len(trajectory.records)}) -- was "
            f"this Trajectory built from a different directory?"
        )

    timestamps_path = data_dir.parent / "timestamps.txt"
    all_timestamps = _read_timestamps(timestamps_path)

    source_origin = trajectory.records[0]
    origin_lat, origin_lon = coordinate_origin or (
        source_origin.lat,
        source_origin.lon,
    )
    scale = math.cos(math.radians(origin_lat))
    origin_alt = source_origin.alt
    origin_time: datetime | None = None

    samples: list[CanonicalSample] = []
    for sample_index, (frame_index, record) in enumerate(zip(frame_indices, trajectory.records)):
        if frame_index >= len(all_timestamps):
            raise KittiParseError(f"{timestamps_path}: no timestamp entry for frame {frame_index}")
        frame_time = all_timestamps[frame_index]
        if origin_time is None:
            origin_time = frame_time

        if coordinate_origin is not None:
            # Use the same projection as the static roads, with the origin
            # provided by SessionManager for this scene-linked simulation.
            x, y = _latlon_to_scene_xy(
                record.lat, record.lon, origin_lat, origin_lon
            )
        else:
            # Preserve the original KITTI projection for standalone playback.
            x, y = _latlon_to_local_xy(
                record.lat,
                record.lon,
                origin_lat,
                origin_lon,
                scale,
            )
        heading = (90.0 - math.degrees(record.yaw)) % 360.0
        speed = math.hypot(record.vf, record.vl)

        samples.append(CanonicalSample(
            sample_index=sample_index,
            source_frame=frame_index,
            source_timestamp=frame_time.isoformat(timespec="milliseconds") + "Z",
            simulation_time=(frame_time - origin_time).total_seconds(),
            position=Position(x=x, y=y, z=record.alt - origin_alt),
            orientation=Orientation(heading=heading, pitch=math.degrees(record.pitch), roll=math.degrees(record.roll)),
            speed=speed,
        ))
    return samples
# -----------------------------------------------------------------------------------
# Function: nearest_sample
# -----------------------------------------------------------------------------------
"""
Description:
    This function finds the CanonicalSample whose simulation_time is
    closest to the requested simulation time.

    It is intended for fixed-tick or time-based playback where the requested
    simulation time does not necessarily correspond exactly to a stored
    trajectory sample.

Arguments:
    samples: List of normalized CanonicalSample objects.
    simulation_time: Target simulation time in seconds.

Returns:
    The CanonicalSample with the smallest absolute difference from the
    requested simulation time.

Raises:
    ValueError: If the samples list is empty.
"""
def nearest_sample(samples: list[CanonicalSample], simulation_time: float) -> CanonicalSample:
    """Looks up the sample closest to a given simulation_time, for fixed-tick playback."""
    if not samples:
        raise ValueError("Cannot look up a sample in an empty trajectory")
    return min(samples, key=lambda sample: abs(sample.simulation_time - simulation_time))
