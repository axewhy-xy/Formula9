"""
Plots a KITTI drive's ego-vehicle trajectory, for progress screenshots.

Author: Choo Jian Wei
Date Created: 15/09/2026
Last Modified: 15/09/2026

Description:
    Runs the project's own kitti_loader.load_trajectory and
    trajectory_normalizer.normalize_trajectory against a real, locally
    downloaded KITTI drive (see scripts/download_kitti.py), then plots 
    the resulting local x/y positions as a top-down path (with start/end markers), 
    and saves the plot as a PNG in docs/trajectory_plots/.

Usage:
    python scripts/visualize_trajectory.py --drive 2011_09_26_drive_0001

"""

import argparse
import sys
from pathlib import Path

import matplotlib.pyplot as plt

sys.path.insert(0, str(Path(__file__).parent.parent / "backend"))

from src.datasets.kitti_loader import load_trajectory
from src.datasets.trajectory_normalizer import normalize_trajectory


"""
# -----------------------------------------------------------------------------------
# Function: find_oxts_data_dir
# -----------------------------------------------------------------------------------
Description:
    This function locates the oxts/data directory inside a downloaded
    drive's folder. KITTI's zip files extract into a nested structure
    (data/kitti/<drive>/<date>/<drive>_sync/oxts/data), so this searches
    for it rather than assuming an exact fixed path.

Args:
    drive_name (str): The drive's name, e.g. "2011_09_26_drive_0001",
        matching the folder created by download_kitti.py.

Returns:
    Path: Path to the drive's oxts/data directory.

Raises:
    FileNotFoundError: If no oxts/data directory is found under the
        drive's folder -- most likely because download_kitti.py hasn't
        been run for this drive yet.

"""
def find_oxts_data_dir(drive_name: str) -> Path:
    drive_root = Path(__file__).parent.parent / "data" / "kitti" / drive_name
    matches = list(drive_root.rglob("oxts/data"))
    if not matches:
        raise FileNotFoundError(
            f"No oxts/data found under {drive_root} -- did you run "
            f"download_kitti.py --drive {drive_name} first?"
        )
    return matches[0]


"""
# -----------------------------------------------------------------------------------
# Function: main
# -----------------------------------------------------------------------------------
Description:
    This function is the script's entry point. It parses the --drive
    argument, loads and normalizes that drive's trajectory using the
    project's existing datasets pipeline, plots the resulting local x/y
    positions as a top-down path (with start/end markers), and saves the
    plot as a PNG in docs/trajectory_plots/.

Args:
    None (reads sys.argv via argparse).

Returns:
    None

"""
def main() -> None:
    parser = argparse.ArgumentParser(description="Plot a KITTI drive's trajectory.")
    parser.add_argument("--drive", required=True, help="Drive name, e.g. 2011_09_26_drive_0001")
    args = parser.parse_args()

    oxts_data_dir = find_oxts_data_dir(args.drive)
    trajectory = load_trajectory(oxts_data_dir, sequence_id=args.drive)
    samples = normalize_trajectory(trajectory, oxts_data_dir)

    xs = [sample.position.x for sample in samples]
    ys = [sample.position.y for sample in samples]

    fig, ax = plt.subplots(figsize=(8, 8))
    ax.plot(xs, ys, "-", linewidth=1.5, color="#2b6cb0", label="Trajectory")
    ax.plot(xs[0], ys[0], "go", markersize=10, label="Start")
    ax.plot(xs[-1], ys[-1], "rs", markersize=10, label="End")
    ax.set_xlabel("x (m, east-ish)")
    ax.set_ylabel("y (m, north-ish)")
    ax.set_title(f"Ego vehicle trajectory -- {args.drive}\n{len(samples)} frames")
    ax.set_aspect("equal")
    ax.grid(True, alpha=0.3)
    ax.legend()

    out_dir = Path(__file__).parent.parent / "docs" / "trajectory_plots"
    out_dir.mkdir(parents=True, exist_ok=True)
    out_path = out_dir / f"{args.drive}_trajectory.png"
    fig.savefig(out_path, dpi=150, bbox_inches="tight")
    print(f"Saved: {out_path.resolve()}")
    print(f"Frames plotted: {len(samples)}")
    print(f"Total distance covered: x range [{min(xs):.1f}, {max(xs):.1f}]m, y range [{min(ys):.1f}, {max(ys):.1f}]m")


if __name__ == "__main__":
    main()