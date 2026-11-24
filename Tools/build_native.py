"""Build the backend's native background traffic engine (backend/native/traffic).

    python Tools/build_native.py [--python backend/.venv/Scripts/python.exe] [--check]

Compiles src.traffic._traffic_native with CMake and the C++ compiler found on this machine (Visual Studio
on Windows), against the backend's Python and its pinned nanobind, and copies the module into
backend/src/traffic, where the backend loads it. The backend runs the same traffic in Python when the module
is missing or out of date, so this is optional: it makes each session's ticks several times cheaper.

--check only reports whether the module in backend/src/traffic is built from the current source.
Exits 0 when the module is built (or current, with --check), 1 otherwise.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "backend" / "native" / "traffic"
TARGET = ROOT / "backend" / "src" / "traffic"
BUILD = ROOT / ".tools" / "native-build" / "traffic"


def default_python() -> Path:
    return ROOT / "backend" / ".venv" / ("Scripts/python.exe" if os.name == "nt" else "bin/python")


def source_hash() -> str:
    return hashlib.sha256((SOURCE / "traffic_core.cpp").read_bytes()).hexdigest()


def built_hash(python: Path) -> str | None:
    """The source hash the installed module reports, read by the backend's own Python."""
    probe = ("import sys; sys.path.insert(0, sys.argv[1]);\n"
             "try:\n from src.traffic import _traffic_native as m; print(m.SOURCE_HASH)\n"
             "except ImportError: pass")
    result = subprocess.run([str(python), "-I", "-c", probe, str(ROOT / "backend")], capture_output=True, text=True)
    return result.stdout.strip() or None


def build(python: Path) -> int:
    if shutil.which("cmake") is None:
        print("CMake was not found, so the native traffic engine was not built; the backend drives its "
              "traffic in Python. Install CMake and a C++ compiler (Visual Studio's \"Desktop development "
              "with C++\") to build it.", file=sys.stderr)
        return 1
    BUILD.mkdir(parents=True, exist_ok=True)
    configure = ["cmake", "-S", str(SOURCE), "-B", str(BUILD), f"-DPython_EXECUTABLE={python}"]
    if subprocess.call(configure):
        print("Configuring the native traffic engine failed (above); the backend drives its traffic in Python.",
              file=sys.stderr)
        return 1
    if subprocess.call(["cmake", "--build", str(BUILD), "--config", "Release", "--parallel"]):
        print("Building the native traffic engine failed (above); the backend drives its traffic in Python.",
              file=sys.stderr)
        return 1
    suffix = ".pyd" if os.name == "nt" else ".so"
    built = [path for path in BUILD.rglob(f"_traffic_native*{suffix}") if path.is_file()]
    if not built:
        print(f"The build made no _traffic_native{suffix} under {BUILD}.", file=sys.stderr)
        return 1
    module = max(built, key=lambda path: path.stat().st_mtime)
    # Copy beside the target and rename over it: a running backend may hold the old module open, and
    # Windows then refuses the replacement, which is reported instead of leaving half a file
    TARGET.mkdir(parents=True, exist_ok=True)
    destination = TARGET / module.name
    with tempfile.NamedTemporaryFile(dir=TARGET, suffix=".tmp", delete=False) as handle:
        staged = Path(handle.name)
    shutil.copy2(module, staged)
    try:
        os.replace(staged, destination)
    except PermissionError:
        staged.unlink(missing_ok=True)
        print(f"{destination} is in use (is the backend running?). Stop it and build again.", file=sys.stderr)
        return 1
    for stale in TARGET.glob(f"_traffic_native*{suffix}"):
        if stale != destination:
            stale.unlink(missing_ok=True)
    print(f"Built the native traffic engine: {destination.relative_to(ROOT)}", flush=True)
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--python", type=Path, default=default_python(), help="The backend's Python (default backend/.venv)")
    parser.add_argument("--check", action="store_true", help="Only report whether the built module is current")
    args = parser.parse_args()
    python = args.python.resolve()
    if not python.is_file():
        parser.error(f"{python} does not exist; run Tools/run_backend.py --setup first")
    if args.check:
        current = built_hash(python) == source_hash()
        print("The native traffic engine is " + ("built from the current source." if current else "missing or out of date."))
        return 0 if current else 1
    return build(python)


if __name__ == "__main__":
    raise SystemExit(main())
