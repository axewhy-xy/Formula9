"""
The dataset-independent sensor frame that every perception data source produces.

Author: Choo Jian Wei
Date Created: 09/10/2026
Last Modified: 09/10/2026

Description:
    Perception code (projection, object distance, world coordinates) only
    works with SensorFrame objects, never with dataset files directly. Each
    dataset gets its own source in perception/sources/ (e.g. kitti.py) that
    reads its own file layout and formats and returns SensorFrames.

    Replacing the dataset therefore means writing one new FrameSource; the
    rest of the perception pipeline is unchanged.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

import numpy as np


# -----------------------------------------------------------------------------------
# Class: SensorFrame
# -----------------------------------------------------------------------------------
"""
Description:
    This class holds one moment of synchronised camera and LiDAR data, in
    the form the generic perception code expects.

Attributes:
    frame_id: The dataset's frame number.
    image_path: Path to the camera image.
    points: (N, 3) LiDAR points x, y, z in metres, in the LiDAR's own frame.
    lidar_to_image: 3x4 matrix projecting [x, y, z, 1] LiDAR points to pixels.
    image_size: Image (width, height) in pixels.
"""
@dataclass(frozen=True)
class SensorFrame:
    """One synchronised camera image and LiDAR scan, ready for projection."""
    frame_id: int
    image_path: Path
    points: np.ndarray
    lidar_to_image: np.ndarray
    image_size: tuple[int, int]


# -----------------------------------------------------------------------------------
# Class: FrameSource
# -----------------------------------------------------------------------------------
"""
Description:
    The interface every dataset source provides. Any class with these two
    methods counts as a FrameSource (a Protocol needs no inheritance).

Methods:
    frame_ids: The frame numbers available, in order.
    load_frame: Loads one frame as a SensorFrame.
"""
class FrameSource(Protocol):
    def frame_ids(self) -> list[int]: ...

    def load_frame(self, frame_id: int) -> SensorFrame: ...
