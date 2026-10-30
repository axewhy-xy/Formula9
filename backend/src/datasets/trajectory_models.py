"""
Data models for parsed KITTI OXTS (GPS/IMU) trajectory data.

Author: Choo Jian Wei
Date Created: 14/09/2026
Last Modified: 14/09/2026

Description:
    This module defines the structured, typed representations produced by
    kitti_loader.py. These are plain data containers only -- no parsing, no
    file I/O, and no coordinate conversion. Coordinate transforms (e.g.
    lat/lon -> local x/y) belong in the ingestion/transform stage
    downstream, not here.

"""

from __future__ import annotations

from dataclasses import dataclass
"""
# -----------------------------------------------------------------------------------
# Class: OxtsRecord
# -----------------------------------------------------------------------------------
Description:
    This class represents a single OXTS (GPS/IMU) reading, corresponding to
    one line of a KITTI oxts/data/*.txt file. Field names and order follow
    KITTI's own oxts/dataformat.txt specification exactly, and units match
    KITTI's documentation: lat/lon in degrees, alt in metres, roll/pitch/yaw
    in radians, velocities in m/s, accelerations in m/s^2, angular rates in
    rad/s. Values are stored exactly as parsed -- no unit conversion here.

Attributes:
    lat, lon, alt: Global position (WGS84 latitude/longitude in degrees,
        altitude in metres).
    roll, pitch, yaw: Orientation (radians).
    vn, ve: Velocity north/east (m/s).
    vf, vl, vu: Forward/leftward/upward velocity in the vehicle frame (m/s).
    ax, ay, az: Acceleration in the vehicle frame (m/s^2).
    af, al, au: Forward/leftward/upward acceleration (m/s^2).
    wx, wy, wz: Angular rate in the vehicle frame (rad/s).
    wf, wl, wu: Forward/leftward/upward angular rate (rad/s).
    pos_accuracy: Position accuracy, north/east (metres).
    vel_accuracy: Velocity accuracy, north/east (m/s).
    navstat: Navigation status of the GPS receiver.
    numsats: Number of satellites tracked by the GPS receiver.
    posmode: Position mode of the GPS receiver.
    velmode: Velocity mode of the GPS receiver.
    orimode: Orientation mode of the GPS receiver.

"""
@dataclass(frozen=True)
class OxtsRecord:
    lat: float
    lon:float
    alt:float
    roll:float
    pitch:float
    yaw:float
    vn:float
    ve:float
    vf:float
    vl:float
    vu:float
    ax:float
    ay:float
    az:float
    af:float
    al:float
    au:float
    wx:float
    wy:float
    wz:float
    wf:float
    wl:float
    wu:float
    pos_accuracy: float
    vel_accuracy: float
    navstat: int
    numsats: int
    posmode: int
    velmode: int
    orimode: int
"""
# -----------------------------------------------------------------------------------
# Class: Trajectory
# -----------------------------------------------------------------------------------
Description:
    This class represents an ordered sequence of OxtsRecord frames for a
    single KITTI drive sequence, as produced by kitti_loader.load_trajectory.

Attributes:
    sequence_id: Identifier for the source KITTI drive (e.g. the drive
        folder name), kept for traceability back to the raw data.
    records: The parsed OxtsRecord frames, in filename-sorted (i.e.
        chronological) order.

"""
@dataclass(frozen=True)
class Trajectory:
    sequence_id: str
    records: list[OxtsRecord]
    def __len__(self) -> int:
        return len(self.records)
# -----------------------------------------------------------------------------------
# Class: Position
# -----------------------------------------------------------------------------------
"""
Description:
    This class represents a local Cartesian position in metres relative to
    the trajectory's reference frame, typically the first frame.

Attributes:
    x: Position along the local x-axis in metres.
    y: Position along the local y-axis in metres.
    z: Position along the local z-axis in metres.
"""
@dataclass(frozen=True)
class Position:
    """Local Cartesian position in metres, relative to a trajectory's first frame."""
    x: float
    y: float
    z: float
# -----------------------------------------------------------------------------------
# Class: Orientation
# -----------------------------------------------------------------------------------
"""
Description:
    This class represents the vehicle's normalized orientation in degrees.
    Heading follows the canonical convention of 0 degrees pointing north
    and increasing clockwise.

Attributes:
    heading: Vehicle heading in degrees.
    pitch: Vehicle pitch in degrees.
    roll: Vehicle roll in degrees.
"""
@dataclass(frozen=True)
class Orientation:
    """Vehicle orientation in degrees. heading: 0=north, clockwise (converted
    from OXTS yaw — confirm this convention against the frozen contract)."""
    heading: float
    pitch: float
    roll: float
# -----------------------------------------------------------------------------------
# Class: CanonicalSample
# -----------------------------------------------------------------------------------
"""
Description:
    This class represents one normalized trajectory sample following the
    project's common M1 data contract. It contains the normalized vehicle
    state required by downstream simulation and visualization systems.

Attributes:
    sample_index: Sequential index of the normalized sample.
    source_frame: Original KITTI frame number.
    source_timestamp: Timestamp associated with the source KITTI frame.
    simulation_time: Simulation time of the sample in seconds.
    position: Local Cartesian position in metres.
    orientation: Normalized vehicle orientation in degrees.
    speed: Vehicle speed in metres per second.
    speed_unit: Unit used for the speed value, defaulting to "m/s".
"""
@dataclass(frozen=True)
class CanonicalSample:
    """One normalized trajectory sample matching the M1 common contract shape."""
    sample_index: int
    source_frame: int
    source_timestamp: str
    simulation_time: float
    position: Position
    orientation: Orientation
    speed: float
    speed_unit: str = "m/s"