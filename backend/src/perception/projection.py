"""
Projects 3D LiDAR points onto a camera image.

Author: Choo Jian Wei
Date Created: 08/10/2026
Last Modified: 09/10/2026

Description:
    Dataset-independent: works with any points and 3x4 LiDAR-to-image
    matrix, such as those in a SensorFrame (see frame.py). Points behind
    the camera or outside the image are dropped; a mask maps each kept
    pixel back to its original LiDAR point.
"""

from __future__ import annotations

import numpy as np


"""
# -----------------------------------------------------------------------------------
# Function: project_to_image
# -----------------------------------------------------------------------------------
Description:
    This function projects LiDAR points into the image. Points behind the
    camera or outside the image are dropped. The returned mask lines up
    with the input points, so later steps can map each kept pixel back to
    its original 3D LiDAR point.

Args:
    points (np.ndarray): (N, 3) or (N, 4) LiDAR points; extra columns are ignored.
    matrix (np.ndarray): 3x4 LiDAR-to-image matrix.
    width (int): Image width in pixels.
    height (int): Image height in pixels.

Returns:
    tuple[np.ndarray, np.ndarray, np.ndarray]:
        uv: (K, 2) pixel positions (u = column, v = row) of the kept points.
        depth: (K,) distance in front of the camera, in metres.
        mask: (N,) True for each input point that was kept.
"""
def project_to_image(points:np.ndarray, matrix: np.ndarray, width:int, height:int)-> tuple[np.ndarray, np.ndarray, np.ndarray]:
    xyz = points [:, :3]
    homogeneous = np.hstack([xyz, np.ones((len(xyz),1))])
    projected = homogeneous @ matrix.T
    depth = projected[:,2]
    in_front = depth>0
    u = np.full(len(points),-1.0)
    v = np.full(len(points), -1.0)
    u[in_front] = projected[in_front,0]/depth[in_front]
    v[in_front] = projected[in_front,1]/depth[in_front]
    mask = in_front & (u>=0) & (u < width) & (v >=0) & (v< height)
    uv = np.column_stack([u[mask],v[mask]]) #(K,2)
    return uv, depth[mask], mask
