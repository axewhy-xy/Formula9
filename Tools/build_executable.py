"""
Build Formula9.exe with the backend bundled beside it.

Author: Lau Xiang Yang
Date Created: 06/11/2026
Last Modified: 06/11/2026

Description:
    A plain Unity build only contains the client. This script builds the Windows player in batch
    mode, then adds a Backend folder holding a portable copy of Python 3.12 with the backend's
    packages, the backend source, its fixtures and backend/.env, and the shared contracts. The
    player starts that backend when it opens and stops it when it closes (BundledBackend.cs), so
    the build folder runs on a machine with no Python, Unity or repository.

        python Tools/build_executable.py [--output Builds/Formula9] [--zip] [--skip-unity]
        Windows shortcut: build.bat

    Run run.bat once first: it installs the pinned Unity editor and creates backend/.venv. Close the
    project in Unity before building, since batch mode cannot open a project that is already open.
    The build includes backend/.env, which holds the team's MongoDB connection, so share the build
    only with people who may use that database.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request

import run_backend

ROOT = run_backend.ROOT
DEFAULT_OUTPUT = ROOT / "Builds" / "Formula9"
EXECUTABLE = "Formula9.exe"
BACKEND_FOLDER = "Backend"  # BundledBackend.FolderName
UNITY_LOG = ROOT / "Logs" / "build-player.log"
# Standard library parts the server never imports.
UNUSED_STDLIB = {"site-packages", "test", "idlelib", "tkinter", "turtledemo", "ensurepip", "lib2to3"}


"""
# -----------------------------------------------------------------------------------
# Function: base_python
# -----------------------------------------------------------------------------------
Description:
    This function finds the Python installation backend/.venv was created from. A venv's own
    python.exe only redirects to it, so that installation is what the build copies.

Returns:
    Path: The folder holding the base python.exe.

Raises:
    SystemExit: If the venv or its base installation is missing.
"""
def base_python() -> Path:
    home = ""
    for line in (run_backend.ENVIRONMENT / "pyvenv.cfg").read_text(encoding="utf-8").splitlines():
        key, _, value = line.partition("=")
        if key.strip() == "home":
            home = value.strip()
    base = Path(home)
    if not home or not (base / "python.exe").is_file() or not (base / "Lib" / "os.py").is_file():
        raise SystemExit(f"backend/.venv was made from {home or 'an unknown Python'}, which is not a complete "
                         "Python installation that can be copied. Recreate the venv with the python.org installer's "
                         "Python 3.12 (delete backend/.venv and run run.bat).")
    return base


"""
# -----------------------------------------------------------------------------------
# Function: safe_to_replace
# -----------------------------------------------------------------------------------
Description:
    This function checks that a folder about to be deleted is empty or an earlier build, so a
    mistyped --output never wipes anything else.

Args:
    folder (Path): The folder to check.

Returns:
    bool: True if the folder is missing, empty, or looks like an earlier build.
"""
def safe_to_replace(folder: Path) -> bool:
    if not folder.exists():
        return True
    if not folder.is_dir():
        return False
    names = {entry.name for entry in folder.iterdir()}
    return not names or EXECUTABLE in names or BACKEND_FOLDER in names


"""
# -----------------------------------------------------------------------------------
# Function: build_player
# -----------------------------------------------------------------------------------
Description:
    This function builds the Windows player into output with the pinned Unity editor in batch mode.

Args:
    output (Path): The build folder.

Raises:
    SystemExit: If the editor is missing, the project is open in Unity, or the build fails.
"""
def build_player(output: Path) -> None:
    version, _ = run_backend.project_unity_version()
    editor = run_backend.find_editor(version, run_backend.unity_install_roots())
    if editor is None:
        raise SystemExit(f"Unity {version} is not installed. Run run.bat once to install it, then build again.")
    if run_backend.project_open_in_unity():
        raise SystemExit("The project is open in Unity. Close it and build again; batch mode cannot open it twice.")
    UNITY_LOG.parent.mkdir(exist_ok=True)
    print(f"Building the player with Unity {version}. This takes several minutes; log: {UNITY_LOG}", flush=True)
    result = subprocess.call([str(editor), "-batchmode", "-quit", "-projectPath", str(ROOT), "-buildTarget", "Win64",
                              "-executeMethod", "Formula9.EditorTools.PlayerBuild.BuildWindows",
                              "-buildPath", str(output / EXECUTABLE), "-logFile", str(UNITY_LOG)], cwd=ROOT)
    if result or not (output / EXECUTABLE).is_file():
        tail = UNITY_LOG.read_text(encoding="utf-8", errors="replace").splitlines()[-40:] if UNITY_LOG.is_file() else []
        print("\n".join(tail), file=sys.stderr)
        raise SystemExit(f"The Unity build failed (exit code {result}); the full log is {UNITY_LOG}.")
    # Burst's debug symbols are for crash investigation on this machine; Unity says not to ship them.
    for symbols in output.glob("*_DoNotShip"):
        shutil.rmtree(symbols)


"""
# -----------------------------------------------------------------------------------
# Function: bundle_backend
# -----------------------------------------------------------------------------------
Description:
    This function writes the Backend folder: a portable Python (the venv's base installation with
    the venv's packages), the backend source, fixtures and .env, the contracts, and serve.py. The
    folders keep the repository's layout, because the backend finds its files relative to itself.

Args:
    backend (Path): The Backend folder to create.
"""
def bundle_backend(backend: Path) -> None:
    base = base_python()
    print(f"Bundling the backend with Python from {base}", flush=True)
    python = backend / "python"
    python.mkdir(parents=True)
    for entry in base.iterdir():
        if entry.is_file() and (entry.suffix.lower() in {".exe", ".dll"} or entry.name == "LICENSE.txt"):
            shutil.copy2(entry, python)
    shutil.copytree(base / "DLLs", python / "DLLs")
    shutil.copytree(base / "Lib", python / "Lib", ignore=lambda folder, names: [
        name for name in names if Path(folder) == base / "Lib" and name in UNUSED_STDLIB])
    shutil.copytree(run_backend.ENVIRONMENT / "Lib" / "site-packages", python / "Lib" / "site-packages",
                    ignore=shutil.ignore_patterns("pip", "pip-*"))

    source_cache = shutil.ignore_patterns("__pycache__", "*.pyc")
    shutil.copytree(ROOT / "backend" / "src", backend / "backend" / "src", ignore=source_cache)
    shutil.copytree(ROOT / "backend" / "fixtures", backend / "backend" / "fixtures", ignore=source_cache)
    if (ROOT / "backend" / ".env").is_file():
        shutil.copy2(ROOT / "backend" / ".env", backend / "backend" / ".env")
    else:
        print("backend/.env is missing, so the build's account endpoints will answer 503.", file=sys.stderr)
    shutil.copytree(ROOT / "contracts", backend / "contracts", ignore=source_cache)
    shutil.copy2(Path(__file__).with_name("bundled_backend.py"), backend / "serve.py")
    (backend / "state").mkdir()


"""
# -----------------------------------------------------------------------------------
# Function: smoke_test
# -----------------------------------------------------------------------------------
Description:
    This function starts the bundled backend the way the player does, from outside the repository
    with a clean environment, on a free port with temporary session data and no MongoDB, and waits
    for /api/health. It proves the copy runs on its own rather than on files from this checkout.

Args:
    backend (Path): The Backend folder.

Raises:
    SystemExit: If the backend does not answer within two minutes.
"""
def smoke_test(backend: Path) -> None:
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    environment = {key: value for key, value in os.environ.items()
                   if key not in {"PYTHONHOME", "PYTHONPATH", "VIRTUAL_ENV"}}
    # Drop this checkout's venv from PATH, so nothing from it can be picked up.
    environment["PATH"] = os.pathsep.join(path for path in environment.get("PATH", "").split(os.pathsep)
                                          if path and not path.lower().startswith(str(run_backend.ENVIRONMENT).lower()))
    environment["PYTHONNOUSERSITE"] = "1"
    environment["MONGODB_URI"] = ""
    print(f"Checking the bundled backend on port {port}...", flush=True)
    with tempfile.TemporaryDirectory(prefix="formula9-build-check-") as scratch:
        environment["TRAINING_DATABASE_PATH"] = str(Path(scratch) / "sessions.sqlite")
        log = Path(scratch) / "backend.log"
        server = subprocess.Popen([str(backend / "python" / "python.exe"), str(backend / "serve.py"),
                                   "--port", str(port), "--parent-pid", str(os.getpid()), "--log", str(log)],
                                  cwd=scratch, env=environment)
        try:
            deadline = time.monotonic() + 120
            while time.monotonic() < deadline:
                if server.poll() is not None:
                    break
                try:
                    with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/health", timeout=2) as reply:
                        if reply.status == 200:
                            print("The bundled backend answered /api/health.", flush=True)
                            return
                except OSError:
                    time.sleep(0.5)
            print(log.read_text(encoding="utf-8", errors="replace") if log.is_file() else "", file=sys.stderr)
            raise SystemExit("The bundled backend did not start; its output is above.")
        finally:
            server.kill()
            server.wait(timeout=10)


"""
# -----------------------------------------------------------------------------------
# Function: folder_size
# -----------------------------------------------------------------------------------
Description:
    This function adds up the sizes of every file under a folder.

Args:
    folder (Path): The folder to measure.

Returns:
    int: The total size in bytes.
"""
def folder_size(folder: Path) -> int:
    return sum(path.stat().st_size for path in folder.rglob("*") if path.is_file())


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT, help="Build folder (default: Builds/Formula9)")
    parser.add_argument("--zip", action="store_true", help="Also pack the build folder into a .zip beside it")
    parser.add_argument("--skip-unity", action="store_true",
                        help="Keep the player already in the build folder and only bundle the backend again")
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("The executable is a Windows build; run this on Windows.")
    if sys.version_info[:2] != (3, 12):
        parser.error("Run this with Python 3.12, for example backend/.venv's (build.bat does).")
    output = args.output.resolve()

    if not run_backend.environment_python().exists():
        print("Setting up the backend environment first. This takes a few minutes.", flush=True)
        if run_backend.set_up(run_backend.backend_environment()):
            return 1
    base_python()  # fail before the long Unity build, not after it

    if args.skip_unity:
        if not (output / EXECUTABLE).is_file():
            parser.error(f"--skip-unity needs an existing build, but {output / EXECUTABLE} is missing.")
    else:
        if not safe_to_replace(output):
            parser.error(f"{output} is not empty and is not an earlier build; choose another --output.")
        if output.exists():
            shutil.rmtree(output)
        build_player(output)

    backend = output / BACKEND_FOLDER
    if backend.exists():
        shutil.rmtree(backend)
    bundle_backend(backend)
    smoke_test(backend)

    print(f"\nBuilt {output / EXECUTABLE} ({folder_size(output) / 2**20:.0f} MB). "
          "Copy the whole folder; Formula9.exe starts the backend in its Backend folder.", flush=True)
    if args.zip:
        archive = shutil.make_archive(str(output), "zip", root_dir=output.parent, base_dir=output.name)
        print(f"Packed {archive} ({Path(archive).stat().st_size / 2**20:.0f} MB).", flush=True)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        raise SystemExit(130)
