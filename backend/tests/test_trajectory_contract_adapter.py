"""
Test cases for the trajectory contract adapter.

Author: Lau Xiang Yang
Date: 15/09/2026
Last Modified: 15/09/2026

Description:
This module contains test cases for the trajectory contract adapter, 
ensuring that the conversion from internal trajectory representation 
to the contract representation is accurate and adheres to the expected conventions.

"""

from pathlib import Path

import pytest

from backend.src.datasets.kitti_loader import load_trajectory
from backend.src.datasets.trajectory_contract_adapter import (
    to_contract_coordinates,
    to_contract_point,
)
from backend.src.datasets.trajectory_models import (
    CanonicalSample,
    Orientation as InternalOrientation,
    Position,
    Trajectory,
)


FIXTURES_DIR = Path(__file__).parent / "fixtures" / "sample_oxts"


def test_to_contract_coordinates_uses_first_record_as_origin():
    trajectory = load_trajectory(FIXTURES_DIR, sequence_id="test_sequence")

    coordinates = to_contract_coordinates(trajectory)

    assert coordinates.origin.latitude == pytest.approx(trajectory.records[0].lat)
    assert coordinates.origin.longitude == pytest.approx(trajectory.records[0].lon)
    assert coordinates.x_axis == "east"
    assert coordinates.y_axis == "north"
    assert coordinates.heading_zero_axis == "positive_x"
    assert coordinates.heading_positive_direction == "counter_clockwise"


def test_to_contract_coordinates_rejects_empty_trajectory():
    trajectory = Trajectory(sequence_id="empty", records=[])

    with pytest.raises(ValueError, match="empty trajectory"):
        to_contract_coordinates(trajectory)

"""
Test cases for the trajectory contract adapter.

# ---------------------------------------------------------------------------
Function: test_to_contract_point_converts_heading_convention
# ---------------------------------------------------------------------------
Description:
    Tests the conversion of heading from internal representation to contract representation.
    The internal heading is defined as 0 degrees = east, counter-clockwise, while the contract
    heading is defined as 0 degrees = north, clockwise.

Parameters:
    internal_heading (float): The heading in the internal representation.
    expected_contract_heading (float): The expected heading in the contract representation.

Returns:
    None
"""
@pytest.mark.parametrize(
    ("internal_heading", "expected_contract_heading"),
    [
        (0.0, 90.0),    # north
        (90.0, 0.0),    # east
        (180.0, 270.0), # south
        (270.0, 180.0), # west
    ],
)

def test_to_contract_point_converts_heading_convention(
    internal_heading,
    expected_contract_heading,
):
    sample = CanonicalSample(
        sample_index=7,
        source_frame=12,
        source_timestamp="2011-09-26T13:02:25.964Z",
        simulation_time=1.2,
        position=Position(x=1.0, y=2.0, z=3.0),
        orientation=InternalOrientation(
            heading=internal_heading,
            pitch=4.0,
            roll=5.0,
        ),
        speed=6.0,
    )

    point = to_contract_point(sample)

    assert point.orientation.heading == pytest.approx(
        expected_contract_heading
    )
    assert point.index == sample.sample_index
