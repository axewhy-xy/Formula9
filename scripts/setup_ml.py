"""
Installs the optional machine-learning dependencies used for perception and
reinforcement-learning work (PyTorch, torchvision).

Author: Choo Jian Wei
Date Created: 06/10/2026
Last Modified: 06/10/2026

Description:
    Installs the packages pinned in backend/requirements-ml.txt into the
    Python interpreter that runs this script, choosing the right build for
    this machine:

      - NVIDIA GPU with a driver supporting CUDA 12.6 or newer: the CUDA
        build exactly as pinned.
      - No NVIDIA GPU (or --cpu): the CPU build of the same versions, from
        PyTorch's CPU index. Slower, but enough for tests and short runs.
      - NVIDIA GPU with an older driver: stops and asks for a driver update
        (or --cpu), instead of installing a build that cannot use the GPU.

    Afterwards it imports torch in a fresh process and reports whether the
    GPU is usable. With --download-weights it also caches the Faster R-CNN
    detector weights (~170 MB) so the first detector run does not stall.

    This is not part of run.bat or CI: the backend runs without these
    packages.

Usage:
    python scripts/setup_ml.py
    python scripts/setup_ml.py --cpu
    python scripts/setup_ml.py --download-weights

"""

import argparse
import re
import shutil
import subprocess
import sys
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parent.parent
REQUIREMENTS_FILE = PROJECT_ROOT / "backend" / "requirements-ml.txt"
CPU_INDEX_URL = "https://download.pytorch.org/whl/cpu"
REQUIRED_CUDA = (12, 6)


"""
# -----------------------------------------------------------------------------------
# Function: detect_driver_cuda_version
# -----------------------------------------------------------------------------------
Description:
    Asks nvidia-smi which CUDA version the installed NVIDIA driver
    supports. This is the newest CUDA build the driver can run, not a
    CUDA toolkit install (PyTorch wheels bundle their own CUDA runtime).

Parameters:
    None.

Returns:
    (major, minor), or None if there is no NVIDIA GPU/driver.
"""
def detect_driver_cuda_version() -> tuple[int, int] | None:
    if shutil.which("nvidia-smi") is None:
        return None
    try:
        output = subprocess.run(
            ["nvidia-smi"], capture_output=True, text=True, check=True, timeout=30
        ).stdout
    except (subprocess.SubprocessError, OSError):
        return None
    match = re.search(r"CUDA Version:\s*(\d+)\.(\d+)", output)
    if match is None:
        return None
    return int(match.group(1)), int(match.group(2))


"""
# -----------------------------------------------------------------------------------
# Function: read_pinned_packages
# -----------------------------------------------------------------------------------
Description:
    Reads the package pins from requirements-ml.txt, skipping comments and
    pip options such as --extra-index-url.

Parameters:
    None.

Returns:
    Pins as written, e.g. ["torch==2.14.1+cu126", "torchvision==0.29.1+cu126"].
"""
def read_pinned_packages() -> list[str]:
    packages = []
    for line in REQUIREMENTS_FILE.read_text(encoding="utf-8").splitlines():
        line = line.split("#", 1)[0].strip()
        if line and not line.startswith("-"):
            packages.append(line)
    return packages


"""
# -----------------------------------------------------------------------------------
# Function: install
# -----------------------------------------------------------------------------------
Description:
    Runs pip with this interpreter, so packages land in the same Python
    that ran the script.

Parameters:
    use_cpu: Install the CPU build instead of the pinned CUDA build.

Returns:
    None. Exits the script if pip fails.
"""
def install(use_cpu: bool) -> None:
    pip = [sys.executable, "-m", "pip", "install", "--disable-pip-version-check"]
    if use_cpu:
        # "torch==2.14.1+cu126" -> "torch==2.14.1", which matches 2.14.1+cpu
        # on the CPU index, so both builds share one set of version pins.
        packages = [package.split("+", 1)[0] for package in read_pinned_packages()]
        command = pip + ["--index-url", CPU_INDEX_URL] + packages
    else:
        command = pip + ["--requirement", str(REQUIREMENTS_FILE)]

    print("Running:", " ".join(command), "\n", flush=True)
    if subprocess.run(command).returncode != 0:
        message = "pip install failed; see the output above."
        if windows_long_paths_disabled():
            message += (
                "\nWindows long paths are disabled, and torch contains files nested deeply"
                "\nenough to pass the 260-character limit. If pip reported 'No such file or"
                "\ndirectory', install into a Python or venv with a shorter path, or ask an"
                "\nadministrator to enable long paths (LongPathsEnabled)."
            )
        sys.exit(message)


"""
# -----------------------------------------------------------------------------------
# Function: windows_long_paths_disabled
# -----------------------------------------------------------------------------------
Description:
    Reads (never changes) the Windows LongPathsEnabled setting. When it is
    off, paths are limited to 260 characters, which a deeply placed venv
    can exceed while installing torch.

Parameters:
    None.

Returns:
    True only on Windows when long paths are known to be disabled.
"""
def windows_long_paths_disabled() -> bool:
    if sys.platform != "win32":
        return False
    import winreg

    try:
        with winreg.OpenKey(
            winreg.HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Control\FileSystem"
        ) as key:
            return winreg.QueryValueEx(key, "LongPathsEnabled")[0] == 0
    except OSError:
        return True


"""
# -----------------------------------------------------------------------------------
# Function: run_in_fresh_python
# -----------------------------------------------------------------------------------
Description:
    Runs a snippet in a new Python process, so it sees the packages pip
    just installed rather than anything already imported here.

Parameters:
    code: Python source to run.

Returns:
    True if the snippet exited successfully.
"""
def run_in_fresh_python(code: str) -> bool:
    sys.stdout.flush()  # keep our headings above the child process's output
    return subprocess.run([sys.executable, "-c", code]).returncode == 0


VERIFY_CODE = """
import torch, torchvision
print("torch      ", torch.__version__)
print("torchvision", torchvision.__version__)
print("GPU usable ", torch.cuda.is_available())
if torch.cuda.is_available():
    print("GPU        ", torch.cuda.get_device_name(0))
"""

WEIGHTS_CODE = """
from torchvision.models.detection import FasterRCNN_ResNet50_FPN_V2_Weights
FasterRCNN_ResNet50_FPN_V2_Weights.DEFAULT.get_state_dict(progress=True)
print("Detector weights cached.")
"""


"""
# -----------------------------------------------------------------------------------
# Function: main
# -----------------------------------------------------------------------------------
Description:
    Chooses the GPU or CPU build, installs it, verifies the import, and
    optionally caches the detector weights.

Parameters:
    None (reads sys.argv via argparse).

Returns:
    None.
"""
def main() -> None:
    parser = argparse.ArgumentParser(description="Install the optional ML dependencies.")
    parser.add_argument("--cpu", action="store_true", help="install the CPU-only build")
    parser.add_argument(
        "--download-weights",
        action="store_true",
        help="also cache the Faster R-CNN detector weights (~170 MB)",
    )
    args = parser.parse_args()

    print(f"Python: {sys.executable} ({sys.version.split()[0]})")
    if sys.version_info[:2] != (3, 12):
        print("Warning: the project is pinned to Python 3.12.")

    use_cpu = args.cpu
    if not use_cpu:
        cuda_version = detect_driver_cuda_version()
        if cuda_version is None:
            print("No NVIDIA GPU found: installing the CPU build.")
            use_cpu = True
        elif cuda_version < REQUIRED_CUDA:
            sys.exit(
                f"Your NVIDIA driver supports CUDA {cuda_version[0]}.{cuda_version[1]}, "
                f"but this build needs {REQUIRED_CUDA[0]}.{REQUIRED_CUDA[1]} or newer.\n"
                "Update the NVIDIA driver, or rerun with --cpu."
            )
        else:
            print(
                f"NVIDIA driver supports CUDA {cuda_version[0]}.{cuda_version[1]}: "
                "installing the GPU build."
            )

    install(use_cpu)

    print("\nVerifying install:")
    if not run_in_fresh_python(VERIFY_CODE):
        sys.exit("torch was installed but could not be imported; see the error above.")

    if args.download_weights:
        print("\nDownloading detector weights:")
        if not run_in_fresh_python(WEIGHTS_CODE):
            sys.exit("Downloading the detector weights failed; see the error above.")

    print("\nDone.")


if __name__ == "__main__":
    main()
