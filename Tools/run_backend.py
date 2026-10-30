"""Set up, test or serve the repository-local FastAPI backend.

    python Tools/run_backend.py --setup
    python Tools/run_backend.py --test
    python Tools/run_backend.py [--reload] [--host 127.0.0.1] [--port 8000]

Setup installs the existing dependency lock into backend/.venv. It does not
install system software or download the optional KITTI/ML datasets and models.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import venv

ROOT = Path(__file__).resolve().parents[1]
ENVIRONMENT = ROOT / "backend" / ".venv"


def environment_python() -> Path:
    return ENVIRONMENT / ("Scripts/python.exe" if os.name == "nt" else "bin/python")


def backend_environment() -> dict[str, str]:
    environment = os.environ.copy()
    environment["PYTHONPATH"] = os.pathsep.join(
        [str(ROOT / "backend"), str(ROOT), *filter(None, [environment.get("PYTHONPATH")])]
    )
    return environment


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--setup", action="store_true", help="Create backend/.venv and install the dependency lock")
    mode.add_argument("--test", action="store_true", help="Run the backend tests with temporary session data and no MongoDB")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8000)
    parser.add_argument("--reload", action="store_true", help="Watch backend source and shared contracts only")
    args = parser.parse_args()
    if sys.version_info < (3, 12):
        parser.error("Python 3.12 or newer is required; the dependency lock targets Python 3.12.")
    if not 1 <= args.port <= 65535:
        parser.error("--port must be between 1 and 65535")
    environment = backend_environment()
    if args.setup:
        if not environment_python().exists():
            venv.EnvBuilder(with_pip=True).create(ENVIRONMENT)
        return subprocess.call([str(environment_python()), "-m", "pip", "install", "-r",
                                str(ROOT / "backend/requirements.lock.txt")], cwd=ROOT, env=environment)
    interpreter = str(environment_python()) if environment_python().exists() else sys.executable
    if args.test:
        scratch_root = ROOT / ".tools"
        scratch_root.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="backend-tests-", dir=scratch_root) as scratch:
            environment["MONGODB_URI"] = ""
            environment["TRAINING_DATABASE_PATH"] = str(Path(scratch) / "sessions.sqlite")
            environment["TEMP"] = environment["TMP"] = scratch
            return subprocess.call([interpreter, "-m", "pytest", "backend/tests", "-q", "-p", "no:cacheprovider",
                                    "--basetemp", str(Path(scratch) / "pytest")], cwd=ROOT, env=environment)
    command = [interpreter, "-m", "uvicorn", "src.app:app", "--host", args.host, "--port", str(args.port)]
    if args.reload:
        command += ["--reload", "--reload-dir", str(ROOT / "backend/src"), "--reload-dir", str(ROOT / "contracts")]
    print("Serving this repository's backend. Run with --setup first if dependencies are missing.", flush=True)
    return subprocess.call(command, cwd=ROOT, env=environment)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        raise SystemExit(130)
