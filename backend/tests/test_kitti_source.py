"""
Unit tests for perception/sources/kitti.py.

Author: Choo Jian Wei
Date: 08/10/2026
Last Modified: 09/10/2026

Description:
    Tests KITTI calibration parsing, matrix building, velodyne loading,
    finding frame files, and KittiFrameSource. Every test writes its own
    small fake files into a temporary folder, so no KITTI data is needed
    and the tests run in CI. The generic projection maths is tested in
    test_projection.py.

"""

import numpy as np
import pytest

from backend.src.perception.frame import SensorFrame
from backend.src.perception.sources.kitti import (
    KittiFrameSource,
    find_calibration_dir,
    find_frame_files,
    load_calibration,
    load_velodyne_points,
    read_calib_file,
    velo_to_image_matrix,
)

# A hand-made calibration whose projections can be worked out on paper.
# The rotation only swaps axes (LiDAR x forward, y left, z up -> camera x right,
# y down, z forward), there is no translation or rectification, and the camera
# has focal length 100 px with its centre at (50, 40) in a 100x80 image.
AXIS_SWAP = "0 -1 0 0 0 -1 1 0 0"
FOCAL = 100.0
CENTRE_U = 50.0
CENTRE_V = 40.0
WIDTH = 100
HEIGHT = 80
VELO_TO_CAM_LINES = {
    "R": AXIS_SWAP,
    "T": "0 0 0",
}
CAM_TO_CAM_LINES = {
    "R_rect_00": "1 0 0 0 1 0 0 0 1",
    "P_rect_02": f"{FOCAL} 0 {CENTRE_U} 0 0 {FOCAL} {CENTRE_V} 0 0 0 1 0",
    "S_rect_02": f"{WIDTH} {HEIGHT}",
}


"""
# -----------------------------------------------------------------------------------
# Function: write_calibration
# -----------------------------------------------------------------------------------
Description:
    Writes both fake calibration files into a folder, in the same format
    as KITTI's (including a calib_time line), optionally leaving one key
    out so error handling can be tested.

Args:
    folder (Path): Where to write the files.
    omit (str | None): A key to leave out of whichever file contains it.

Returns:
    Path: The folder, ready to pass to load_calibration.
"""
def write_calibration(folder, omit=None):
    for file_name, lines in (
        ("calib_velo_to_cam.txt", VELO_TO_CAM_LINES),
        ("calib_cam_to_cam.txt", CAM_TO_CAM_LINES),
    ):
        text = "calib_time: 15-Mar-2012 11:37:16\n"
        text += "".join(f"{key}: {value}\n" for key, value in lines.items() if key != omit)
        (folder / file_name).write_text(text)
    return folder


"""
# -----------------------------------------------------------------------------------
# Function: test_read_calib_file_skips_text_and_keeps_order
# -----------------------------------------------------------------------------------
Description:
    calib_time is not numeric so it is skipped, and values keep the order
    they were written in.

"""
def test_read_calib_file_skips_text_and_keeps_order(tmp_path):
    calib_file = tmp_path / "calib_velo_to_cam.txt"
    calib_file.write_text(
        "calib_time: 15-Mar-2012 11:37:16\n"
        "R: 1 2 3 4 5 6 7 8 9\n"
        "T: 0.1 0.2 0.3\n"
    )

    result = read_calib_file(calib_file)

    assert "calib_time" not in result
    np.testing.assert_array_equal(result["R"], [1, 2, 3, 4, 5, 6, 7, 8, 9])
    np.testing.assert_allclose(result["T"], [0.1, 0.2, 0.3])


"""
# -----------------------------------------------------------------------------------
# Function: test_read_calib_file_skips_blank_and_colonless_lines
# -----------------------------------------------------------------------------------
Description:
    Blank lines and lines without a colon are ignored instead of crashing.

"""
def test_read_calib_file_skips_blank_and_colonless_lines(tmp_path):
    calib_file = tmp_path / "calib.txt"
    calib_file.write_text("\nnot a calibration line\nT: 1 2 3\n\n")

    result = read_calib_file(calib_file)

    assert list(result) == ["T"]


"""
# -----------------------------------------------------------------------------------
# Function: test_load_calibration_builds_matrices
# -----------------------------------------------------------------------------------
Description:
    R and T go into the right parts of the 4x4 matrix (rotation top-left,
    translation in the last column, [0, 0, 0, 1] bottom row), and the
    other matrices and the image size have the right shapes and values.

"""
def test_load_calibration_builds_matrices(tmp_path):
    velo_lines = dict(VELO_TO_CAM_LINES, T="0.5 -0.25 2")
    (tmp_path / "calib_velo_to_cam.txt").write_text(
        "".join(f"{key}: {value}\n" for key, value in velo_lines.items())
    )
    (tmp_path / "calib_cam_to_cam.txt").write_text(
        "".join(f"{key}: {value}\n" for key, value in CAM_TO_CAM_LINES.items())
    )

    calib = load_calibration(tmp_path)

    np.testing.assert_array_equal(
        calib.velo_to_cam,
        [
            [0, -1, 0, 0.5],
            [0, 0, -1, -0.25],
            [1, 0, 0, 2],
            [0, 0, 0, 1],
        ],
    )
    np.testing.assert_array_equal(calib.r_rect, np.eye(4))
    assert calib.p_rect.shape == (3, 4)
    assert calib.p_rect[0, 0] == FOCAL
    assert calib.p_rect[1, 2] == CENTRE_V
    assert calib.image_size == (WIDTH, HEIGHT)
    assert all(isinstance(value, int) for value in calib.image_size)


"""
# -----------------------------------------------------------------------------------
# Function: test_load_calibration_reports_missing_key
# -----------------------------------------------------------------------------------
Description:
    A missing key raises a ValueError naming the key and the file it should
    be in, instead of a bare KeyError.

"""
@pytest.mark.parametrize(
    ("missing_key", "file_name"),
    [
        ("R", "calib_velo_to_cam.txt"),
        ("T", "calib_velo_to_cam.txt"),
        ("R_rect_00", "calib_cam_to_cam.txt"),
        ("P_rect_02", "calib_cam_to_cam.txt"),
        ("S_rect_02", "calib_cam_to_cam.txt"),
    ],
)
def test_load_calibration_reports_missing_key(tmp_path, missing_key, file_name):
    write_calibration(tmp_path, omit=missing_key)

    with pytest.raises(ValueError, match=missing_key) as error:
        load_calibration(tmp_path)

    assert file_name in str(error.value)


"""
# -----------------------------------------------------------------------------------
# Function: test_load_velodyne_points_round_trip
# -----------------------------------------------------------------------------------
Description:
    Points written as raw float32 values come back unchanged, one row per
    point.

"""
def test_load_velodyne_points_round_trip(tmp_path):
    points = np.array(
        [[10.0, 1.5, -0.5, 0.2], [-3.0, 4.0, 1.0, 0.9]], dtype=np.float32
    )
    scan = tmp_path / "0000000000.bin"
    points.tofile(scan)

    loaded = load_velodyne_points(scan)

    assert loaded.shape == (2, 4)
    assert loaded.dtype == np.float32
    np.testing.assert_array_equal(loaded, points)


"""
# -----------------------------------------------------------------------------------
# Function: test_load_velodyne_points_rejects_truncated_file
# -----------------------------------------------------------------------------------
Description:
    A file whose value count is not a multiple of 4 is not a valid scan
    (e.g. a partial download) and raises a ValueError.

"""
def test_load_velodyne_points_rejects_truncated_file(tmp_path):
    scan = tmp_path / "0000000000.bin"
    np.array([1.0, 2.0, 3.0, 4.0, 5.0], dtype=np.float32).tofile(scan)

    with pytest.raises(ValueError):
        load_velodyne_points(scan)


"""
# -----------------------------------------------------------------------------------
# Function: test_velo_to_image_matrix_combines_in_order
# -----------------------------------------------------------------------------------
Description:
    With the hand-made calibration, P_rect @ R_rect @ Tr_velo_to_cam can be
    worked out on paper. A wrong multiplication order gives different
    numbers (or a shape error).

"""
def test_velo_to_image_matrix_combines_in_order(tmp_path):
    calib = load_calibration(write_calibration(tmp_path))

    np.testing.assert_allclose(
        velo_to_image_matrix(calib),
        [
            [CENTRE_U, -FOCAL, 0.0, 0.0],
            [CENTRE_V, 0.0, -FOCAL, 0.0],
            [1.0, 0.0, 0.0, 0.0],
        ],
    )


"""
# -----------------------------------------------------------------------------------
# Function: write_fake_drive
# -----------------------------------------------------------------------------------
Description:
    Builds a tiny fake KITTI drive in the same nested layout the real
    download produces, with the hand-made calibration, a placeholder image
    per frame and a small velodyne scan per frame.

Args:
    drive_root (Path): The fake drive's folder.
    image_frames (list[int]): Frames that get an image_02 file.
    scan_frames (list[int]): Frames that get a velodyne scan.

Returns:
    Path: drive_root, ready to pass to KittiFrameSource.
"""
def write_fake_drive(drive_root, image_frames=(0, 1, 2), scan_frames=(0, 1, 2)):
    date_dir = drive_root / "2011_09_26"
    sync_dir = date_dir / "2011_09_26_drive_0001_sync"
    image_dir = sync_dir / "image_02" / "data"
    scan_dir = sync_dir / "velodyne_points" / "data"
    image_dir.mkdir(parents=True)
    scan_dir.mkdir(parents=True)
    write_calibration(date_dir)
    for frame in image_frames:
        (image_dir / f"{frame:010d}.png").write_bytes(b"not a real image")
    for frame in scan_frames:
        # One point per frame, 10 m ahead and frame metres to the left.
        np.array([10.0, float(frame), 0.0, 0.5], dtype=np.float32).tofile(
            scan_dir / f"{frame:010d}.bin"
        )
    return drive_root


"""
# -----------------------------------------------------------------------------------
# Function: test_find_calibration_dir
# -----------------------------------------------------------------------------------
Description:
    Finds the nested date folder holding the calibration files, and
    reports a clear error when there are none.

"""
def test_find_calibration_dir(tmp_path):
    drive_root = write_fake_drive(tmp_path / "drive")

    assert find_calibration_dir(drive_root) == drive_root / "2011_09_26"
    with pytest.raises(FileNotFoundError):
        find_calibration_dir(tmp_path / "empty")


"""
# -----------------------------------------------------------------------------------
# Function: test_find_frame_files_uses_ten_digit_names
# -----------------------------------------------------------------------------------
Description:
    Frame 2 is found as 0000000002.png / 0000000002.bin, and a frame with
    no files raises FileNotFoundError.

"""
def test_find_frame_files_uses_ten_digit_names(tmp_path):
    drive_root = write_fake_drive(tmp_path)

    image_path, scan_path = find_frame_files(drive_root, 2)

    assert image_path.name == "0000000002.png"
    assert image_path.parent.parent.name == "image_02"
    assert scan_path.name == "0000000002.bin"
    with pytest.raises(FileNotFoundError):
        find_frame_files(drive_root, 99)


"""
# -----------------------------------------------------------------------------------
# Function: test_frame_ids_needs_image_and_scan
# -----------------------------------------------------------------------------------
Description:
    Only frames with both a camera image and a velodyne scan are listed,
    in order.

"""
def test_frame_ids_needs_image_and_scan(tmp_path):
    drive_root = write_fake_drive(tmp_path, image_frames=[3, 0, 1], scan_frames=[0, 1, 2])

    assert KittiFrameSource(drive_root).frame_ids() == [0, 1]


"""
# -----------------------------------------------------------------------------------
# Function: test_load_frame_returns_sensor_frame
# -----------------------------------------------------------------------------------
Description:
    load_frame returns a SensorFrame with the frame's image path, its
    points as (N, 3) x, y, z (reflectance dropped), the drive's matrix and
    the image size.

"""
def test_load_frame_returns_sensor_frame(tmp_path):
    source = KittiFrameSource(write_fake_drive(tmp_path))

    frame = source.load_frame(1)

    assert isinstance(frame, SensorFrame)
    assert frame.frame_id == 1
    assert frame.image_path.name == "0000000001.png"
    np.testing.assert_allclose(frame.points, [[10.0, 1.0, 0.0]])
    np.testing.assert_allclose(frame.lidar_to_image, source.lidar_to_image)
    assert frame.image_size == (WIDTH, HEIGHT)


"""
# -----------------------------------------------------------------------------------
# Function: test_load_frame_missing_frame
# -----------------------------------------------------------------------------------
Description:
    Asking for a frame that does not exist raises FileNotFoundError.

"""
def test_load_frame_missing_frame(tmp_path):
    source = KittiFrameSource(write_fake_drive(tmp_path))

    with pytest.raises(FileNotFoundError):
        source.load_frame(99)
