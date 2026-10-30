"""The Unity repository can run and test its backend without the old checkout."""

from pathlib import Path
import sys

from src.core.config import get_settings
from src.services.training_scene import PACKAGE_ROOT, TrainingScenePackage
from Tools import run_backend


ROOT = Path(__file__).resolve().parents[2]


def test_default_paths_are_local_and_independent_of_working_directory(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    monkeypatch.delenv("MAP_DATABASE_PATH", raising=False)
    monkeypatch.delenv("TRAINING_DATABASE_PATH", raising=False)
    get_settings.cache_clear()
    try:
        settings = get_settings()
        assert settings.map_database_path == ROOT / "backend/fixtures/map/map.db"
        assert settings.map_database_path.is_file()
        assert settings.map_database_path.with_name("map.osm").is_file()
        assert settings.training_database_path == ROOT / "state/training.sqlite"
        assert PACKAGE_ROOT == ROOT / "backend/fixtures/m2_scene"
        assert TrainingScenePackage(PACKAGE_ROOT).reference().manifest_sha256
    finally:
        get_settings.cache_clear()


def test_existing_database_paths_can_be_selected_explicitly(tmp_path, monkeypatch):
    database = tmp_path / "existing.sqlite"
    static_map = tmp_path / "map.db"
    monkeypatch.setenv("TRAINING_DATABASE_PATH", str(database))
    monkeypatch.setenv("MAP_DATABASE_PATH", str(static_map))
    get_settings.cache_clear()
    try:
        settings = get_settings()
        assert settings.training_database_path == database
        assert settings.map_database_path == static_map
    finally:
        get_settings.cache_clear()


def test_launcher_watches_only_backend_and_contracts(monkeypatch):
    calls = []
    monkeypatch.setattr(sys, "argv", ["run_backend.py", "--reload", "--port", "8019"])
    monkeypatch.setattr(run_backend, "environment_python", lambda: Path(sys.executable))
    monkeypatch.setattr(run_backend.subprocess, "call", lambda command, **kwargs: calls.append((command, kwargs)) or 0)
    assert run_backend.main() == 0
    command, options = calls[0]
    assert command[:4] == [sys.executable, "-m", "uvicorn", "src.app:app"]
    assert command[command.index("--port") + 1] == "8019"
    watched = [command[i + 1] for i, part in enumerate(command) if part == "--reload-dir"]
    assert watched == [str(ROOT / "backend/src"), str(ROOT / "contracts")]
    assert options["cwd"] == ROOT


def test_launcher_isolates_tests_from_configured_account_and_session_stores(monkeypatch):
    monkeypatch.setenv("MONGODB_URI", "mongodb://unreachable.example/never-used")
    monkeypatch.setenv("TRAINING_DATABASE_PATH", "must-not-be-opened.sqlite")
    monkeypatch.setattr(sys, "argv", ["run_backend.py", "--test"])
    monkeypatch.setattr(run_backend, "environment_python", lambda: Path(sys.executable))
    databases = []

    def inspect(command, *, cwd, env):
        assert command[1:5] == ["-m", "pytest", "backend/tests", "-q"]
        assert cwd == ROOT and env["MONGODB_URI"] == ""
        database = Path(env["TRAINING_DATABASE_PATH"])
        assert database.parent.is_relative_to(ROOT / ".tools")
        assert database.parent.is_dir()
        databases.append(database)
        return 0

    monkeypatch.setattr(run_backend.subprocess, "call", inspect)
    assert run_backend.main() == 0
    assert databases and not databases[0].parent.exists()
