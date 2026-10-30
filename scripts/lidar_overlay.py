"""
Draws a KITTI frame's LiDAR points over its camera image, to check the calibration.

Author: Choo Jian Wei
Date Created: 09/10/2026
Last Modified: 09/10/2026

Description:
    Loads one KITTI frame through KittiFrameSource, projects its LiDAR
    points onto the camera image, and saves an overlay coloured by depth
    to docs/perception_previews/, as a visual check of the calibration.

Usage:
    python scripts/lidar_overlay.py --drive 2011_09_26_drive_0001 --frame 0

"""
import argparse
import sys
from pathlib import Path
import matplotlib.pyplot as plt

sys.path.insert(0,str(Path(__file__).parent.parent/"backend"))
from src.perception.projection import project_to_image
from src.perception.sources.kitti import KittiFrameSource

def main()-> None:
    parser = argparse.ArgumentParser(description=" Loads one KITTI frame through KittiFrameSource.")
    parser.add_argument("--drive", default="2011_09_26_drive_0001", help="KITTI drive name")
    parser.add_argument("--frame", type = int, default=0, help ="Frame number")
    args = parser.parse_args()
    drive_root= Path(__file__).parent.parent/"data"/"kitti"/args.drive
    source = KittiFrameSource(drive_root)
    frame = source.load_frame(args.frame)
    uv , depth, mask = project_to_image(frame.points, frame.lidar_to_image, *frame.image_size)
    print(f"Frame {args.frame}: {mask.sum()} of {len(frame.points)} points land on the image")

    #Image Projection
    image = plt.imread(frame.image_path)
    fig, ax = plt.subplots(figsize=(15,5))
    ax.imshow(image)
    dots = ax.scatter(uv[:,0], uv[:,1], c= depth, cmap= "jet_r", s =1)

    fig.colorbar(dots, ax=ax, label= "Distance (m)", fraction=0.02, pad =0.01)
    ax.set_title(f"{args.drive}, frame {args.frame}: {mask.sum()} LiDAR points")
    ax.axis("off")

    output_dir = Path(__file__).parent.parent/"docs"/"perception_previews"
    output_dir.mkdir (parents=True,exist_ok = True)
    output_path = output_dir / f"{args.drive}_{args.frame:010d}_lidar_overlay.png"
    fig.savefig(output_path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"Saved: {output_path}")

if __name__ == "__main__":
    main()


   

