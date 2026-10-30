"""
Exports GPS coordinates from a KITTI drive's oxts data, for sharing with
whoever is extracting the corresponding OSM road section.
 
Author: Choo Jian Wei
Date Created: 19/09/2026
Last Modified: 19/09/2026
 
Description:
    Reads the raw lat/lon from every frame in a downloaded KITTI drive
    (unlike trajectory_normalizer, this keeps real GPS coordinates -- no
    local x/y conversion, since the OSM road-map extraction needs actual
    lat/lon to query against). Outputs a bounding box (same north/south/
    east/west shape as graph_loader.get_bounds(), so it can be dropped
    straight into an OSMnx bbox query) plus the full per-frame route as a
    list of {"lat": ..., "lon": ...} objects, for an accurate road overlay
    rather than just a bounding box.
 
Usage:
    python scripts/exportgpsroute.py --drive 2011_09_26_drive_0001
 
"""
import argparse
import json
import sys
from pathlib import Path
sys.path.insert (0, str(Path(__file__).parent.parent/"backend"))
from src.datasets.kitti_loader import load_trajectory

"""
# -----------------------------------------------------------------------------------
# Function: find_oxts_data_dir
# -----------------------------------------------------------------------------------
Description:
    This function locates the oxts/data directory inside a downloaded
    drive's folder, searching rather than assuming a fixed nested path
    (KITTI's zip structure varies slightly by extraction).
 
Args:
    drive_name (str): The drive's name, e.g. "2011_09_26_drive_0001".
 
Returns:
    Path: Path to the drive's oxts/data directory.
 
Raises:
    FileNotFoundError: If no oxts/data directory is found, most likely
        because download_kitti.py hasn't been run for this drive yet.
 
"""
def find_oxts_data_dir(drive_name: str)-> Path:
    drive_root = Path(__file__).parent.parent/"data"/"kitti"/drive_name
    matches = list (drive_root.rglob("oxts/data"))
    if not matches:
        raise FileNotFoundError(f"No oxts/data found under {drive_root} -- did you run "
            f"download_kitti.py --drive {drive_name} first?")
    return matches[0]
"""
# -----------------------------------------------------------------------------------
# Function: main
# -----------------------------------------------------------------------------------
Description:
    This function is the script's entry point. It loads a drive's raw
    trajectory, computes the lat/lon bounding box across every frame, and
    writes a JSON file containing the bounding box plus the full per-frame
    GPS route -- ready to send to whoever needs to look up the matching
    OSM road section.
 
Args:
    None (reads sys.argv via argparse).
 
Returns:
    None
 
"""
def main()-> None:
    parser = argparse.ArgumentParser(description="Export a KITTI drive's GPS route.")
    parser.add_argument("--drive",required=True,help= "Drive name, e.g. 2011_09_26_drive_0001")
    parser.add_argument("--out", default= None, help= "Output Json path.",)
    args = parser.parse_args()
    oxts_data_dir = find_oxts_data_dir(args.drive)
    trajectory = load_trajectory(oxts_data_dir, sequence_id=args.drive)
    lats = [record.lat for record in trajectory.records]
    lons = [record.lon for record in trajectory.records]
    output ={
        "drive": args.drive,
        "frame_count": len(trajectory.records),
        "bounds": {
            "north": max(lats),
            "south": min (lats),
            "east": max (lons),
            "west": min (lons),
        },
        "start": {"lat": lats[0], "lon": lons[0]},
        "end": {"lat": lats[-1], "lon": lons[-1]},
        "route": [{"lat": lat, "lon": lon} for lat, lon in zip(lats, lons)],
    }
    out_dir = Path(__file__).parent.parent / "docs" / "gps_exports"
    out_dir.mkdir(parents=True, exist_ok=True)
    out_path = Path(args.out) if args.out else out_dir / f"{args.drive}_gps_route.json"
    out_path.write_text(json.dumps(output, indent=2))
    print(f"Saved: {out_path.resolve()}")
    print(f"Bounds: {output['bounds']}")
    print(f"Frame count : {output['frame_count']}")

if __name__ == "__main__":
    main()



