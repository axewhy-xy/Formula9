"""Set up, test or serve the repository-local FastAPI backend.

    python Tools/run_backend.py --setup
    python Tools/run_backend.py --test
    python Tools/run_backend.py --import-bus-data
    python Tools/run_backend.py --start-everything
    python Tools/run_backend.py [--reload] [--host 127.0.0.1] [--port 8000]

Setup installs the existing dependency lock into backend/.venv. It does not
install system software or download the optional KITTI/ML datasets and models.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import venv

ROOT = Path(__file__).resolve().parents[1]
ENVIRONMENT = ROOT / "backend" / ".venv"
TRANSIT_DATA = ROOT / "backend" / "fixtures" / "transit" / "transit.sqlite"


def environment_python() -> Path:
    return ENVIRONMENT / ("Scripts/python.exe" if os.name == "nt" else "bin/python")


def backend_environment() -> dict[str, str]:
    environment = os.environ.copy()
    # An activated environment or PYTHONPATH from the old checkout must not supply
    # modules to this server. The local venv owns dependencies; these paths own code.
    environment["PYTHONPATH"] = os.pathsep.join([str(ROOT / "backend"), str(ROOT)])
    environment.pop("PYTHONHOME", None)
    environment["PYTHONNOUSERSITE"] = "1"
    previous_environment = environment.pop("VIRTUAL_ENV", None)
    paths = environment.get("PATH", "").split(os.pathsep)
    if previous_environment:
        previous = Path(previous_environment).resolve()
        paths = [p for p in paths if p and not Path(p).resolve().is_relative_to(previous)]
    environment["VIRTUAL_ENV"] = str(ENVIRONMENT)
    environment["PATH"] = os.pathsep.join([str(environment_python().parent), *paths])
    return environment


def set_up(environment: dict[str, str]) -> int:
    if not environment_python().exists():
        venv.EnvBuilder(with_pip=False).create(ENVIRONMENT)
    # Recover cleanly if an earlier setup created the venv but pip failed.
    result = subprocess.call([str(environment_python()), "-m", "ensurepip", "--upgrade"], cwd=ROOT, env=environment)
    if result:
        return result
    return subprocess.call([str(environment_python()), "-m", "pip", "install", "-r",
                            str(ROOT / "backend/requirements.lock.txt")], cwd=ROOT, env=environment)


def saved_value(name: str, path: Path) -> str:
    if not path.is_file():
        return ""
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.strip().startswith(name + "="):
            return line.split("=", 1)[1].strip()
    return ""


def import_bus_data(interpreter: str, environment: dict[str, str]) -> int:
    """Download the LTA bus data with the DataMall key in backend/.env. Never prompts."""
    if not (environment.get("LTA_ACCOUNT_KEY") or saved_value("LTA_ACCOUNT_KEY", ROOT / "backend" / ".env")):
        print("Bus stops need a free LTA DataMall key: https://datamall.lta.gov.sg\n"
              "Put it in backend/.env as LTA_ACCOUNT_KEY=... and run this again.", file=sys.stderr)
        return 2
    return subprocess.call([interpreter, "-m", "src.transit.import_lta"], cwd=ROOT / "backend", env=environment)


def project_unity_version(path: Path = ROOT / "ProjectSettings" / "ProjectVersion.txt") -> tuple[str, str]:
    """The editor version this project is pinned to, and its changeset."""
    match = re.search(r"m_EditorVersionWithRevision: (\S+) \((\w+)\)", path.read_text(encoding="utf-8"))
    if not match:
        raise ValueError(f"No editor version in {path}")
    return match.group(1), match.group(2)


def unity_install_roots() -> list[Path]:
    roots = [Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Unity" / "Hub" / "Editor"]
    # Unity Hub's "Installs location" setting, when someone moved it
    setting = Path(os.environ.get("APPDATA", "")) / "UnityHub" / "secondaryInstallPath.json"
    try:
        custom = json.loads(setting.read_text(encoding="utf-8"))
        if custom:
            roots.insert(0, Path(custom))
    except (OSError, ValueError):
        pass
    return roots


def find_editor(version: str, roots: list[Path]) -> Path | None:
    for root in roots:
        editor = root / version / "Editor" / "Unity.exe"
        if editor.is_file():
            return editor
    return None


def find_hub(roots: list[Path]) -> Path | None:
    places = [Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Unity Hub"]
    places += [root / "Unity Hub" for root in roots]
    for place in places:
        hub = place / "Unity Hub.exe"
        if hub.is_file():
            return hub
    return None


def project_open_in_unity(lock: Path = ROOT / "Temp" / "UnityLockfile") -> bool:
    """Unity holds this file locked while the project is open; a leftover one can be opened."""
    if not lock.is_file():
        return False
    try:
        with open(lock, "r+b"):
            return False
    except OSError:
        return True


def open_in_unity() -> None:
    """Open the project in the exact editor it is pinned to, installing that editor if needed.

    Versions install side by side, so anyone's other Unity versions are left alone.
    """
    if os.name != "nt":
        print("Open this folder in Unity yourself; automatic setup is Windows only.")
        return
    version, changeset = project_unity_version()
    roots = unity_install_roots()
    editor = find_editor(version, roots)
    if editor is None:
        hub = find_hub(roots)
        if hub is None:
            print("Installing Unity Hub…", flush=True)
            subprocess.call(["winget", "install", "--id", "Unity.UnityHub", "-e", "--silent",
                             "--accept-package-agreements", "--accept-source-agreements"])
            hub = find_hub(roots)
        if hub is not None:
            print(f"Installing Unity {version}. This downloads several GB and can take a while.", flush=True)
            subprocess.call([str(hub), "--", "--headless", "install", "--version", version, "--changeset", changeset])
            editor = find_editor(version, unity_install_roots())
    if editor is None:
        print(f"Could not install Unity {version} automatically. Install it from Unity Hub, then run this again.\n"
              f"unityhub://{version}/{changeset}", file=sys.stderr)
        return
    if project_open_in_unity():
        print("This project is already open in Unity.")
        return
    print(f"Opening the project in Unity {version}. If Unity asks, sign in with a free Unity account.", flush=True)
    subprocess.Popen([str(editor), "-projectPath", str(ROOT)], cwd=ROOT, env=os.environ.copy())


def fetch_large_files() -> None:
    """Models, images and audio are in Git LFS; a clone without it holds only placeholders."""
    if not (ROOT / ".git").exists() or shutil.which("git") is None:
        return
    if subprocess.call(["git", "lfs", "install", "--local"], cwd=ROOT) or subprocess.call(["git", "lfs", "pull"], cwd=ROOT):
        print("Could not download the Git LFS files; 3D models and sounds may be missing.", file=sys.stderr)


def stop_previous_backend(port: int) -> None:
    """A backend left running holds the session database and the port, so run.bat replaces it."""
    if os.name != "nt":
        return
    script = ("Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | Where-Object { "
              f"$_.CommandLine -match 'uvicorn src\\.app:app' -and $_.CommandLine -match '--port {port}( |$)' }} | "
              "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; Start-Sleep -Seconds 1")
    subprocess.call(["powershell", "-NoProfile", "-Command", script])


def start_everything(interpreter: str, environment: dict[str, str]) -> None:
    """What run.bat does when double-clicked, before it serves the backend. Never prompts."""
    fetch_large_files()
    # The bus stops are committed; this only matters if the file is ever removed.
    if not TRANSIT_DATA.is_file() and subprocess.call([interpreter, "-m", "src.transit.import_osm"], cwd=ROOT / "backend", env=environment):
        print("Carrying on without bus stops; the map says so when you zoom in.", file=sys.stderr)
    open_in_unity()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--setup", action="store_true", help="Create backend/.venv and install the dependency lock")
    mode.add_argument("--test", action="store_true", help="Run the backend tests with temporary session data and no MongoDB")
    mode.add_argument("--import-bus-data", action="store_true", help="Download the LTA DataMall bus data for the map")
    mode.add_argument("--start-everything", action="store_true", help="Set up everything, open Unity and serve the backend (what double-clicking run.bat does)")
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
        return set_up(environment)
    # The first run sets itself up, so a new clone only needs run.bat.
    if not environment_python().exists():
        print("First run: setting up the backend environment. This takes a few minutes.", flush=True)
        result = set_up(environment)
        if result:
            return result
    interpreter = str(environment_python())
    if args.import_bus_data:
        return import_bus_data(interpreter, environment)
    if args.test:
        scratch_root = ROOT / ".tools"
        scratch_root.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="backend-tests-", dir=scratch_root) as scratch:
            environment["MONGODB_URI"] = ""
            environment["TRAINING_DATABASE_PATH"] = str(Path(scratch) / "sessions.sqlite")
            environment["TEMP"] = environment["TMP"] = scratch
            return subprocess.call([interpreter, "-m", "pytest", "backend/tests", "-q", "-p", "no:cacheprovider",
                                    "--basetemp", str(Path(scratch) / "pytest")], cwd=ROOT, env=environment)
    if args.start_everything:
        stop_previous_backend(args.port)
        start_everything(interpreter, environment)
    command = [interpreter, "-m", "uvicorn", "src.app:app", "--host", args.host, "--port", str(args.port)]
    if args.reload:
        command += ["--reload", "--reload-dir", str(ROOT / "backend/src"), "--reload-dir", str(ROOT / "contracts")]
        # Every reload would start the session worker processes again, each loading the road network:
        # while editing, sessions run in the server's own process unless asked otherwise
        environment.setdefault("F9_SESSION_WORKERS", "0")
    print(f"Repository: {ROOT}\nPython: {interpreter}\nBackend: {ROOT / 'backend'}", flush=True)
    return subprocess.call(command, cwd=ROOT, env=environment)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        raise SystemExit(130)
