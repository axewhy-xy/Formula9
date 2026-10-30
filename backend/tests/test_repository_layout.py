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


def test_launcher_drops_an_old_checkout_from_the_python_environment(tmp_path, monkeypatch):
    old = tmp_path / "Formula_9"
    old_venv = old / "backend/.venv"
    monkeypatch.setenv("PYTHONPATH", str(old / "backend"))
    monkeypatch.setenv("PYTHONHOME", str(old))
    monkeypatch.setenv("VIRTUAL_ENV", str(old_venv))
    monkeypatch.setenv("PATH", str(old_venv / "Scripts"))
    environment = run_backend.backend_environment()
    assert environment["PYTHONPATH"].split(run_backend.os.pathsep) == [str(ROOT / "backend"), str(ROOT)]
    assert "PYTHONHOME" not in environment
    assert environment["VIRTUAL_ENV"] == str(ROOT / "backend/.venv")
    assert str(old) not in environment["PATH"]
    assert environment["PYTHONNOUSERSITE"] == "1"


def test_first_run_sets_up_its_own_environment_instead_of_using_an_old_one(tmp_path, monkeypatch):
    local_python = tmp_path / "missing-python.exe"
    monkeypatch.setattr(sys, "argv", ["run_backend.py"])
    monkeypatch.setattr(run_backend, "environment_python", lambda: local_python)
    commands = []
    monkeypatch.setattr(run_backend.subprocess, "call", lambda command, **kwargs: commands.append(command) or 0)
    monkeypatch.setattr(run_backend, "set_up", lambda environment: 7)
    assert run_backend.main() == 7, "a failed setup stops the launcher"
    assert commands == []
    monkeypatch.setattr(run_backend, "set_up", lambda environment: 0)
    assert run_backend.main() == 0
    assert commands[0][0] == str(local_python), "the server only runs on the local environment"


def test_the_bus_data_import_never_prompts(monkeypatch, capsys):
    monkeypatch.delenv("LTA_ACCOUNT_KEY", raising=False)
    monkeypatch.setattr(run_backend, "saved_value", lambda name, path: "")
    monkeypatch.setattr("builtins.input", lambda *_: (_ for _ in ()).throw(AssertionError("Must not prompt")))
    assert run_backend.import_bus_data("python", {}) == 2
    assert "LTA_ACCOUNT_KEY" in capsys.readouterr().err


def test_the_project_is_opened_in_its_pinned_unity_version(tmp_path):
    version_file = tmp_path / "ProjectVersion.txt"
    version_file.write_text("m_EditorVersion: 6000.1.12f1\nm_EditorVersionWithRevision: 6000.1.12f1 (da0c3ee78ee0)\n", encoding="utf-8")
    assert run_backend.project_unity_version(version_file) == ("6000.1.12f1", "da0c3ee78ee0")
    assert run_backend.find_editor("6000.1.12f1", [tmp_path]) is None
    other_version = tmp_path / "2022.3.1f1" / "Editor"
    other_version.mkdir(parents=True)
    (other_version / "Unity.exe").write_text("")
    assert run_backend.find_editor("6000.1.12f1", [tmp_path]) is None, "another version never stands in"
    pinned = tmp_path / "6000.1.12f1" / "Editor"
    pinned.mkdir(parents=True)
    (pinned / "Unity.exe").write_text("")
    assert run_backend.find_editor("6000.1.12f1", [tmp_path]) == pinned / "Unity.exe"


def test_a_leftover_unity_lockfile_does_not_count_as_open(tmp_path):
    lock = tmp_path / "UnityLockfile"
    assert not run_backend.project_open_in_unity(lock)
    lock.write_text("")
    assert not run_backend.project_open_in_unity(lock)
