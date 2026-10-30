"""
KITTI Raw Data Retrieval Script

Author: Choo Jian Wei

Description:
    Downloads a single KITTI raw drive (synced+rectified camera images,
    Velodyne LiDAR, GPS/IMU) plus its date's calibration archive, and
    extracts them into data/kitti/<drive_name>/. This script is what gets
    committed to git -- the actual downloaded/extracted dataset files
    never are. data/ is gitignored; every teammate runs this script
    locally to get their own copy rather than pulling it via git.

    URL pattern confirmed from KITTI's own official raw_data_downloader.sh
    (bundled with the raw dataset download script on the raw data page):
        https://s3.eu-central-1.amazonaws.com/avg-kitti/raw_data/<drive>/<drive>_sync.zip
        https://s3.eu-central-1.amazonaws.com/avg-kitti/raw_data/<date>_calib.zip
    These are public S3 objects -- no login needed to fetch the files
    themselves, only to access the raw data page listing them.

Usage:
    python scripts/download_kitti.py --drive 2011_09_26_drive_0001
    python scripts/download_kitti.py --velodyne-calib
"""

import argparse
import os
import shutil
import urllib.request
import zipfile

S3_BASE_URL = "https://s3.eu-central-1.amazonaws.com/avg-kitti/raw_data"

# Sensor-level Velodyne factory calibration (per-laser corrections). Public,
# no registration required -- unlike a drive, this is not per-sequence.
VELODYNE_FACTORY_CALIB_URL = (
    "https://www.cvlibs.net/datasets/kitti/downloads/"
    "velodynecalib_S2_factory_flatness_intensity.xml"
)

DATA_DIR = os.path.join(os.path.dirname(__file__), "..", "data", "kitti")


"""
# ------------------------------------------------------------------------------------
# Function: download_file
# ------------------------------------------------------------------------------------
Description:
This function downloads a single file from a URL to a local destination path,
creating parent directories as needed, and skips the download if the file already
exists locally. Uses a browser-like User-Agent since some servers (e.g. cvlibs.net)
reject urllib's default one with a 403.

Args:
    url (str): The URL to download from.
    dest_path (str): The local file path to save to.

Returns:
    None

"""
def download_file(url: str, dest_path: str) -> None:
    if os.path.exists(dest_path):
        print(f"Already downloaded: {dest_path}")
        return
    os.makedirs(os.path.dirname(dest_path), exist_ok=True)
    part_path = dest_path + ".part"
    print(f"Downloading {url} -> {dest_path}")
    request = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    try:
        with urllib.request.urlopen(request) as response, open(part_path, "wb") as out_file:
            shutil.copyfileobj(response, out_file)
        os.replace(part_path, dest_path)
    except Exception:
        # Leave no partial file at dest_path -- only a fully-downloaded file
        # ever gets renamed into place, so a future run's existence check
        # can never mistake a partial download for a complete one.
        if os.path.exists(part_path):
            os.remove(part_path)
        raise


"""
# ------------------------------------------------------------------------------------
# Function: extract_and_clean
# ------------------------------------------------------------------------------------
Description:
This function extracts a zip archive into a target directory, then deletes the
original archive to avoid keeping both the compressed and extracted copies on disk.

Args:
    zip_path (str): Path to the downloaded zip archive.
    extract_to (str): Directory to extract the archive's contents into.

Returns:
    None

"""
def extract_and_clean(zip_path: str, extract_to: str) -> None:
    print(f"Extracting {zip_path} -> {extract_to}")
    with zipfile.ZipFile(zip_path, "r") as zip_ref:
        zip_ref.extractall(extract_to)
    os.remove(zip_path)


"""
# ------------------------------------------------------------------------------------
# Function: fetch_drive
# ------------------------------------------------------------------------------------
Description:
This function downloads and extracts a KITTI drive's sync archive (images,
LiDAR, GPS/IMU) and its date's calibration archive into data/kitti/<drive_name>/,
using KITTI's official public S3 URL pattern -- no manual URL lookup needed.

Args:
    drive_name (str): Full drive name, e.g. "2011_09_26_drive_0001".

Returns:
    str: The local directory containing the downloaded KITTI drive.

"""
def fetch_drive(drive_name: str) -> str:
    date = "_".join(drive_name.split("_")[:3])
    drive_dir = os.path.join(DATA_DIR, drive_name)
    marker_path = os.path.join(drive_dir, ".download_complete")

    if os.path.exists(marker_path):
        print(f"Already downloaded and extracted: {drive_dir}")
        return drive_dir

    try:
        sync_url = f"{S3_BASE_URL}/{drive_name}/{drive_name}_sync.zip"
        sync_zip = os.path.join(drive_dir, f"{drive_name}_sync.zip")
        download_file(sync_url, sync_zip)
        extract_and_clean(sync_zip, drive_dir)

        calib_url = f"{S3_BASE_URL}/{date}_calib.zip"
        calib_zip = os.path.join(drive_dir, f"{date}_calib.zip")
        download_file(calib_url, calib_zip)
        extract_and_clean(calib_zip, drive_dir)

        with open(marker_path, "w") as marker_file:
            marker_file.write("ok\n")

        print(f"Done. Drive data available at: {drive_dir}")
        return drive_dir

    except Exception:
        if os.path.exists(drive_dir):
            shutil.rmtree(drive_dir)
        raise


"""
# ------------------------------------------------------------------------------------
# Function: fetch_velodyne_factory_calib
# ------------------------------------------------------------------------------------
Description:
This function downloads the publicly-available Velodyne HDL-64E S2 factory
calibration file (per-laser corrections) into data/kitti/calibration/. Unlike
fetch_drive, this requires no registration and applies to the sensor itself,
not to any single recorded drive.

Args:
    None

Returns:
    None

"""
def fetch_velodyne_factory_calib() -> None:
    dest_path = os.path.join(DATA_DIR, "calibration", "velodyne_factory_calib.xml")
    download_file(VELODYNE_FACTORY_CALIB_URL, dest_path)
    print(f"Done. Velodyne factory calibration available at: {dest_path}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Fetch KITTI raw data and/or calibration files.")
    parser.add_argument(
        "--drive",
        help="Full drive name, e.g. 2011_09_26_drive_0001",
    )
    parser.add_argument(
        "--velodyne-calib",
        action="store_true",
        help="Also fetch the public Velodyne factory calibration file.",
    )
    args = parser.parse_args()

    if args.velodyne_calib:
        fetch_velodyne_factory_calib()
    if args.drive:
        fetch_drive(args.drive)
    if not args.velodyne_calib and not args.drive:
        parser.error("Specify --drive and/or --velodyne-calib.")