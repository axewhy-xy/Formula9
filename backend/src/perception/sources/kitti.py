"""
Reads KITTI raw drives as perception SensorFrames.

Author: Choo Jian Wei
Date Created: 08/10/2026
Last Modified: 09/10/2026

Description:
    The only perception module that knows KITTI's file layout and formats:
    the calibration text files, velodyne .bin scans, and the
    <drive>/<date>/<drive>_sync/{image_02,velodyne_points}/data/ folders
    with 10-digit frame file names.

    KittiFrameSource turns a downloaded drive into SensorFrames (see
    perception/frame.py) using the left colour camera (image_02). To use a
    different dataset, write a new source alongside this one instead of
    changing the generic perception code.
"""
from __future__ import annotations
from dataclasses import dataclass
from pathlib import Path
import numpy as np

from ..frame import SensorFrame

"""
# -----------------------------------------------------------------------------------
# Function: read_calib_file
# -----------------------------------------------------------------------------------
Description:
    This function reads one KITTI calibration text file, where each line
    has the form "key: numbers". Lines whose values are not numeric (such
    as calib_time) and lines without a colon are skipped.

Args:
    path (Path | str): Path to a calib_*.txt file.

Returns:
    dict[str, np.ndarray]: Each key mapped to a flat array of its values.
"""
def read_calib_file(path: Path | str) -> dict[str, np.ndarray]:
    my_dict = {}
    path= Path(path)
    for line in path.read_text().splitlines():
        if ":" not in line:
            continue
        key, value = line.split(":",1)
        try:
            numbers = np.array([float(x) for x in value.split()])
        except ValueError:
            continue
        my_dict[key.strip()] = numbers
    return my_dict

# -----------------------------------------------------------------------------------
# Class: KittiCalibration
# -----------------------------------------------------------------------------------
"""
Description:
    This class holds the calibration matrices needed to project LiDAR
    points into the image_02 camera for one KITTI recording day.

Attributes:
    velo_to_cam: 4x4 transform from the LiDAR frame to camera 0.
    r_rect: 4x4 rectifying rotation for camera 0 (R_rect_00).
    p_rect: 3x4 projection from the rectified frame to image_02 pixels.
    image_size: Image (width, height) in pixels.
"""
@dataclass(frozen=True)
class KittiCalibration:
    """Matrices for projecting LiDAR points into the image_02 camera."""
    velo_to_cam: np.ndarray
    r_rect: np.ndarray
    p_rect: np.ndarray
    image_size: tuple[int, int]

"""
# -----------------------------------------------------------------------------------
# Function: load_calibration
# -----------------------------------------------------------------------------------
Description:
    This function reads calib_velo_to_cam.txt and calib_cam_to_cam.txt from
    one KITTI date folder and builds the matrices needed to project LiDAR
    points into the image_02 camera.

Args:
    calib_dir (Path | str): The date folder, e.g. data/kitti/<drive>/2011_09_26.

Returns:
    KittiCalibration: The LiDAR-to-camera, rectification and projection
    matrices, plus the image size.

Raises:
    ValueError: If a required key is missing from either file.
"""
def load_calibration(calib_dir: Path | str) -> KittiCalibration:
    velo = read_calib_file(Path(calib_dir)/"calib_velo_to_cam.txt")
    cam = read_calib_file(Path(calib_dir)/"calib_cam_to_cam.txt")
    for key in ("R_rect_00", "P_rect_02", "S_rect_02"):
        if key not in cam:
            raise ValueError(f"Missing {key} in calib_cam_to_cam.txt")
    for key in ("R", "T"):
        if key not in velo:
            raise ValueError(f"Missing {key} in calib_velo_to_cam.txt")
    velo_to_cam = np.eye(4)
    velo_to_cam[:3, :3] = velo["R"].reshape(3,3)
    velo_to_cam[:3, 3] = velo["T"]

    r_rect = np.eye(4)
    r_rect[:3, :3] = cam["R_rect_00"].reshape(3, 3)

    p_rect = cam["P_rect_02"].reshape(3,4)
    image_size= (int(cam["S_rect_02"][0]),int(cam["S_rect_02"][1]))
    return KittiCalibration(velo_to_cam, r_rect, p_rect, image_size)

"""
# -----------------------------------------------------------------------------------
# Function: velo_to_image_matrix
# -----------------------------------------------------------------------------------
Description:
    This function combines the three calibration matrices into one, so a
    LiDAR point reaches image_02 pixels in a single multiplication:
    LiDAR -> camera 0 -> rectified camera -> pixels.

Args:
    calib (KittiCalibration): Calibration from load_calibration.

Returns:
    np.ndarray: 3x4 matrix equal to p_rect @ r_rect @ velo_to_cam.
"""
def velo_to_image_matrix(calib: KittiCalibration) -> np.ndarray:
    return calib.p_rect @ calib.r_rect @ calib.velo_to_cam

"""
# -----------------------------------------------------------------------------------
# Function: load_velodyne_points
# -----------------------------------------------------------------------------------
Description:
    This function reads one KITTI velodyne .bin scan. The file stores raw
    float32 values, four per point: x, y, z (metres, LiDAR frame: x forward,
    y left, z up) and reflectance (0 to 1).

Args:
    path (Path | str): Path to a velodyne_points/data/NNNNNNNNNN.bin file.

Returns:
    np.ndarray: (N, 4) float32 array, one row per point.

Raises:
    ValueError: If the number of values is not a multiple of 4.
"""
def load_velodyne_points(path: Path | str)-> np.ndarray:
    values = np.fromfile(path, dtype=np.float32)
    if values.size % 4 != 0:
        raise ValueError(f"{path} is not a valid velodyne scan: {values.size} values is not a multiple of 4 (x, y, z, reflectance)")
    return values.reshape(-1,4)


"""
# -----------------------------------------------------------------------------------
# Function: find_calibration_dir
# -----------------------------------------------------------------------------------
Description:
    This function finds the folder holding a drive's calibration files.
    KITTI's zips extract into a nested <drive>/<date>/ structure, so this
    searches for calib_velo_to_cam.txt rather than assuming a fixed path.

Args:
    drive_root (Path): The drive's folder, e.g. data/kitti/2011_09_26_drive_0001.

Returns:
    Path: The folder containing the calibration files.

Raises:
    FileNotFoundError: If no calibration files are found.
"""
def find_calibration_dir(drive_root: Path) -> Path:
    matches = list(Path(drive_root).rglob("calib_velo_to_cam.txt"))
    if not matches:
        raise FileNotFoundError(
            f"No KITTI calibration files under {drive_root} -- "
            "run scripts/download_kitti.py for this drive first."
        )
    return matches[0].parent

"""
# -----------------------------------------------------------------------------------
# Function: find_frame_files
# -----------------------------------------------------------------------------------
Description:
    This function finds one frame's left colour camera image (image_02)
    and velodyne scan. KITTI names frame files with 10 zero-padded digits,
    e.g. frame 7 is 0000000007.png / 0000000007.bin.

Args:
    drive_root (Path): The drive's folder, e.g. data/kitti/2011_09_26_drive_0001.
    frame_id (int): The KITTI frame number.

Returns:
    tuple[Path, Path]: The image path and the velodyne scan path.

Raises:
    FileNotFoundError: If either file is missing for this frame.
"""
def find_frame_files(drive_root: Path, frame_id: int) -> tuple[Path, Path]:
    name = f"{frame_id:010d}"
    image_matches = list(Path(drive_root).rglob(f"image_02/data/{name}.png"))
    scan_matches = list(Path(drive_root).rglob(f"velodyne_points/data/{name}.bin"))
    if not image_matches or not scan_matches:
        raise FileNotFoundError(
            f"Frame {frame_id} not found under {drive_root} -- check the frame "
            "number, or run scripts/download_kitti.py for this drive first."
        )
    return image_matches[0], scan_matches[0]

# -----------------------------------------------------------------------------------
# Class: KittiFrameSource
# -----------------------------------------------------------------------------------
"""
Description:
    This class reads one downloaded KITTI raw drive as SensorFrames. The
    calibration is the same for every frame of a drive, so it is loaded
    once when the source is created.

Attributes:
    drive_root: The drive's folder, e.g. data/kitti/2011_09_26_drive_0001.
    calibration: The drive's KittiCalibration.
    lidar_to_image: The combined 3x4 LiDAR-to-image_02 matrix.
"""
class KittiFrameSource:
    """A FrameSource (see perception/frame.py) for one KITTI raw drive."""

    def __init__(self, drive_root: Path | str) -> None:
        self.drive_root = Path(drive_root)
        self.calibration = load_calibration(find_calibration_dir(self.drive_root))
        self.lidar_to_image = velo_to_image_matrix(self.calibration)

    """
    # -------------------------------------------------------------------------------
    # Method: frame_ids
    # -------------------------------------------------------------------------------
    Description:
        Lists the frames that have both a camera image and a velodyne scan.

    Returns:
        list[int]: The frame numbers, in order.
    """
    def frame_ids(self) -> list[int]:
        images = {int(path.stem) for path in self.drive_root.rglob("image_02/data/*.png")}
        scans = {int(path.stem) for path in self.drive_root.rglob("velodyne_points/data/*.bin")}
        return sorted(images & scans)

    """
    # -------------------------------------------------------------------------------
    # Method: load_frame
    # -------------------------------------------------------------------------------
    Description:
        Loads one frame as a SensorFrame.

    Args:
        frame_id (int): The KITTI frame number.

    Returns:
        SensorFrame: The frame's image path, its LiDAR points as (N, 3)
        x, y, z, the drive's LiDAR-to-image matrix and the image size.

    Raises:
        FileNotFoundError: If the frame's image or scan is missing.
    """
    def load_frame(self, frame_id: int) -> SensorFrame:
        image_path, scan_path = find_frame_files(self.drive_root, frame_id)
        all_points = load_velodyne_points(scan_path)
        points = all_points[:, :3]
        return SensorFrame(frame_id=frame_id, image_path=image_path, points=points,lidar_to_image=self.lidar_to_image,image_size= self.calibration.image_size)
