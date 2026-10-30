"""
Adapter for trajectory contract

Author: Lau Xiang Yang
Date: 15/09/2026
Last Modified: 15/09/2026

Description:
This module converts internal trajectory representation to the contract representation.

"""

from contracts.common import (
    CoordinatesMetadata,
    GeographicPoint,
    Orientation,
    Position3D,
    TrajectoryPoint,
)

from .trajectory_models import CanonicalSample, Trajectory

"""
# ---------------------------------------------------------------------------
Function: to_contract_coordinates
# ---------------------------------------------------------------------------
Description:
    Converts a Trajectory to CoordinatesMetadata, using the first GPS position
    as the origin. The x-axis is defined as east, the y-axis as north, and the
    heading is defined such that 0 degrees corresponds to the positive x-axis
    (east) and increases counter-clockwise.

Parameters:
    trajectory (Trajectory): The internal representation of a trajectory.

Returns:
    CoordinatesMetadata: The contract representation of the trajectory's coordinate system.
    
"""
def to_contract_coordinates(trajectory: Trajectory) -> CoordinatesMetadata:
    """Build coordinate metadata using the trajectory's first GPS position."""
    if not trajectory.records:
        raise ValueError("Cannot create coordinate metadata from an empty trajectory")

    origin = trajectory.records[0]
    return CoordinatesMetadata(
        origin=GeographicPoint(
            latitude=origin.lat,
            longitude=origin.lon,
        )
    )

"""
# ---------------------------------------------------------------------------
Function: to_contract_point
# ---------------------------------------------------------------------------
Description:
    Converts a CanonicalSample to a TrajectoryPoint, adapting the internal representation
    to the contract representation. The heading is converted from the internal 
    convention (0 degrees = north, clockwise) to the contract convention
    (0 degrees = positive x/east, counter-clockwise).

Parameters:
    sample (CanonicalSample): The internal representation of a trajectory sample.

Returns:
    TrajectoryPoint: The contract representation of the trajectory sample.
    
"""
def to_contract_point(sample: CanonicalSample) -> TrajectoryPoint:
    return TrajectoryPoint(
        index=sample.sample_index,
        simulation_time=sample.simulation_time,
        position=Position3D(
            x=sample.position.x,
            y=sample.position.y,
            z=sample.position.z,
        ),
        orientation=Orientation(
            heading=(90.0 - sample.orientation.heading) % 360.0,
            pitch=sample.orientation.pitch,
            roll=sample.orientation.roll,
        ),
        speed=sample.speed,
        speed_unit=sample.speed_unit,
        source_frame=sample.source_frame,
        source_timestamp=sample.source_timestamp,
    )
