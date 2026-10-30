"""
Unit tests for perception/projection.py.

Author: Choo Jian Wei
Date: 08/10/2026
Last Modified: 09/10/2026

Description:
    Tests the dataset-independent LiDAR-to-image projection with a
    hand-made 3x4 matrix whose results can be worked out on paper, so these
    tests stay valid whichever dataset the project uses.

"""

import numpy as np

from backend.src.perception.projection import project_to_image

# A hand-made LiDAR-to-image matrix. LiDAR axes (x forward, y left, z up)
# map to camera axes (x right, y down, z forward), with focal length 100 px
# and the image centre at (50, 40) in a 100x80 image:
#   u = 50 - 100 * y / x     v = 40 - 100 * z / x     depth = x
FOCAL = 100.0
CENTRE_U = 50.0
CENTRE_V = 40.0
WIDTH = 100
HEIGHT = 80
MATRIX = np.array(
    [
        [CENTRE_U, -FOCAL, 0.0, 0.0],
        [CENTRE_V, 0.0, -FOCAL, 0.0],
        [1.0, 0.0, 0.0, 0.0],
    ]
)


"""
# -----------------------------------------------------------------------------------
# Function: test_project_to_image_directions
# -----------------------------------------------------------------------------------
Description:
    Straight ahead lands on the image centre at its distance, left moves
    left (smaller u), up moves up (smaller v, since rows count downwards),
    and points behind the camera or off the side of the image are dropped.
    Also checks the mask lines up with the input points.

"""
def test_project_to_image_directions():
    points = np.array(
        [
            [10.0, 0.0, 0.0],    # straight ahead
            [10.0, 1.0, 0.0],    # 1 m left
            [10.0, 0.0, 1.0],    # 1 m up
            [-10.0, 0.0, 0.0],   # behind the camera
            [10.0, -20.0, 0.0],  # far right, off the image
        ]
    )

    uv, depth, mask = project_to_image(points, MATRIX, WIDTH, HEIGHT)

    np.testing.assert_array_equal(mask, [True, True, True, False, False])
    np.testing.assert_allclose(depth, [10.0, 10.0, 10.0])
    np.testing.assert_allclose(
        uv,
        [
            [CENTRE_U, CENTRE_V],
            [CENTRE_U - FOCAL * 1.0 / 10.0, CENTRE_V],
            [CENTRE_U, CENTRE_V - FOCAL * 1.0 / 10.0],
        ],
    )


"""
# -----------------------------------------------------------------------------------
# Function: test_project_to_image_ignores_extra_columns
# -----------------------------------------------------------------------------------
Description:
    (N, 4) points (e.g. with a reflectance column) project the same as
    their (N, 3) x, y, z columns.

"""
def test_project_to_image_ignores_extra_columns():
    with_reflectance = np.array([[10.0, 1.0, 0.5, 0.7], [20.0, -1.0, 0.0, 0.1]])

    uv_4, depth_4, mask_4 = project_to_image(with_reflectance, MATRIX, WIDTH, HEIGHT)
    uv_3, depth_3, mask_3 = project_to_image(with_reflectance[:, :3], MATRIX, WIDTH, HEIGHT)

    np.testing.assert_array_equal(mask_4, mask_3)
    np.testing.assert_allclose(uv_4, uv_3)
    np.testing.assert_allclose(depth_4, depth_3)


"""
# -----------------------------------------------------------------------------------
# Function: test_project_to_image_excludes_image_edges
# -----------------------------------------------------------------------------------
Description:
    Pixels run from 0 to width - 1, so a point landing exactly on u = width
    is off the image, while one landing on u = 0 is on it.

"""
def test_project_to_image_excludes_image_edges():
    # At 10 m, u = 50 - 10 * y, so y = 5 lands on u = 0 and y = -5 on u = 100.
    points = np.array([[10.0, 5.0, 0.0], [10.0, -5.0, 0.0]])

    uv, _, mask = project_to_image(points, MATRIX, WIDTH, HEIGHT)

    np.testing.assert_array_equal(mask, [True, False])
    np.testing.assert_allclose(uv, [[0.0, CENTRE_V]])
