"""
Build Formula9-Setup-<version>.exe, an installer for the bundled Windows build.

Author: Lau Xiang Yang
Date Created: 27/11/2026
Last Modified: 27/11/2026

Description:
    Tools/build_executable.py makes a folder (Builds/Formula9) that runs once copied anywhere. This
    script first makes that folder the same way, then packs it into one Setup.exe with Inno Setup 6
    (Tools/installer/Formula9.iss): it installs Formula9 for the current user without administrator
    rights, adds Start menu and optional desktop shortcuts, and registers an uninstaller. The
    installer goes to Builds/Installer.

        python Tools/build_installer.py [--skip-build] [--skip-unity] [--include-env]
                                        [--build-dir Builds/Formula9] [--output Builds/Installer] [--version 1.0]
        Windows shortcut: installer.bat

    --skip-build packages the build folder as it is; --skip-unity and --include-env go to
    build_executable.py. The version defaults to the Unity project's (Player Settings > Version).
    Inno Setup 6 is installed with winget when it is missing.

    A build that carries backend/.env (the team's MongoDB connection) is only packaged with
    --include-env, because an installer is easier to pass around than a folder.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "Tools" / "installer" / "Formula9.iss"
DEFAULT_BUILD = ROOT / "Builds" / "Formula9"
DEFAULT_OUTPUT = ROOT / "Builds" / "Installer"
PROJECT_SETTINGS = ROOT / "ProjectSettings" / "ProjectSettings.asset"
INNO_WINGET_ID = "JRSoftware.InnoSetup"
INNO_UNINSTALL_KEY = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"


"""
# -----------------------------------------------------------------------------------
# Function: project_version
# -----------------------------------------------------------------------------------
Description:
    This function reads the version set in the Unity project's Player Settings (bundleVersion).

Returns:
    str: The version, or "1.0" if the project does not set one.
"""
def project_version() -> str:
    text = PROJECT_SETTINGS.read_text(encoding="utf-8", errors="replace") if PROJECT_SETTINGS.is_file() else ""
    match = re.search(r"^\s*bundleVersion:\s*(\S+)\s*$", text, re.MULTILINE)
    return match.group(1) if match else "1.0"


"""
# -----------------------------------------------------------------------------------
# Function: inno_candidates
# -----------------------------------------------------------------------------------
Description:
    This function lists the places ISCC.exe, Inno Setup's command-line compiler, may be: where
    the registry says Inno Setup 6 is installed (for all users or just this one), its default
    folders, and PATH.

Returns:
    list[Path]: Possible ISCC.exe paths, most likely first. They may not exist.
"""
def inno_candidates() -> list[Path]:
    found: list[Path] = []
    import winreg
    for hive, view in ((winreg.HKEY_LOCAL_MACHINE, winreg.KEY_WOW64_32KEY),
                       (winreg.HKEY_LOCAL_MACHINE, winreg.KEY_WOW64_64KEY),
                       (winreg.HKEY_CURRENT_USER, 0)):
        try:
            with winreg.OpenKey(hive, INNO_UNINSTALL_KEY, 0, winreg.KEY_READ | view) as key:
                found.append(Path(winreg.QueryValueEx(key, "InstallLocation")[0]) / "ISCC.exe")
        except OSError:
            pass
    for variable in ("ProgramFiles(x86)", "ProgramFiles", "LOCALAPPDATA"):
        base = os.environ.get(variable)
        if base:
            found.append(Path(base) / ("Programs" if variable == "LOCALAPPDATA" else "") / "Inno Setup 6" / "ISCC.exe")
    on_path = shutil.which("ISCC")
    if on_path:
        found.append(Path(on_path))
    return found


"""
# -----------------------------------------------------------------------------------
# Function: find_inno
# -----------------------------------------------------------------------------------
Description:
    This function finds ISCC.exe, installing Inno Setup 6 with winget if it is not installed.

Returns:
    Path: ISCC.exe.

Raises:
    SystemExit: If Inno Setup is missing and winget could not install it.
"""
def find_inno() -> Path:
    for candidate in inno_candidates():
        if candidate.is_file():
            return candidate
    if shutil.which("winget") is None:
        raise SystemExit("Inno Setup 6 is not installed and winget is missing. Install it from "
                         "https://jrsoftware.org/isdl.php, then run this again.")
    print("Installing Inno Setup 6 with winget...", flush=True)
    subprocess.call(["winget", "install", "--id", INNO_WINGET_ID, "-e", "--silent",
                     "--accept-package-agreements", "--accept-source-agreements"])
    for candidate in inno_candidates():
        if candidate.is_file():
            return candidate
    raise SystemExit("Inno Setup 6 could not be installed. Install it from https://jrsoftware.org/isdl.php, "
                     "then run this again.")


"""
# -----------------------------------------------------------------------------------
# Function: check_build
# -----------------------------------------------------------------------------------
Description:
    This function checks that build is a complete bundled build, as Tools/build_executable.py makes
    it, and that it only carries backend/.env when that was asked for.

Args:
    build (Path): The build folder.
    include_env (bool): Whether the installer may ship backend/.env.

Raises:
    SystemExit: If the build is missing parts, or carries .env without include_env.
"""
def check_build(build: Path, include_env: bool) -> None:
    needed = ["Formula9.exe", "Formula9_Data", "UnityPlayer.dll",
              "Backend/serve.py", "Backend/python/python.exe", "Backend/backend/src", "Backend/contracts"]
    missing = [name for name in needed if not (build / name).exists()]
    if missing:
        raise SystemExit(f"{build} is not a complete build (missing {', '.join(missing)}). Build it with build.bat, "
                         "or run this without --skip-build.")
    if (build / "Backend" / "backend" / ".env").is_file() and not include_env:
        raise SystemExit(f"{build} carries backend/.env (the team's MongoDB connection). Package it with "
                         "--include-env only if everyone who gets the installer may use that database; otherwise "
                         "build again without --include-env.")


"""
# -----------------------------------------------------------------------------------
# Function: build_executable
# -----------------------------------------------------------------------------------
Description:
    This function makes the build folder with Tools/build_executable.py, using this Python.

Args:
    build (Path): The build folder.
    skip_unity (bool): Keep the player already in the folder and only bundle the backend again.
    include_env (bool): Ship backend/.env in the build.

Raises:
    SystemExit: If the build fails; build_executable.py has already said why.
"""
def build_executable(build: Path, skip_unity: bool, include_env: bool) -> None:
    command = [sys.executable, str(ROOT / "Tools" / "build_executable.py"), "--output", str(build)]
    if skip_unity:
        command.append("--skip-unity")
    if include_env:
        command.append("--include-env")
    if subprocess.call(command, cwd=ROOT):
        raise SystemExit("The build failed (above), so no installer was made.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--build-dir", type=Path, default=DEFAULT_BUILD, help="Build folder (default: Builds/Formula9)")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT,
                        help="Folder for the installer (default: Builds/Installer)")
    parser.add_argument("--version", default=None, help="Installer version (default: the Unity project's)")
    parser.add_argument("--skip-build", action="store_true", help="Package the build folder as it is")
    parser.add_argument("--skip-unity", action="store_true",
                        help="Passed to build_executable.py: keep the player and only bundle the backend again")
    parser.add_argument("--include-env", action="store_true",
                        help="Ship backend/.env (the team's MongoDB connection) so logging in works; "
                             "give such an installer only to people who may use that database")
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("The installer is for the Windows build; run this on Windows.")
    if args.skip_build and args.skip_unity:
        parser.error("--skip-unity only applies when building; --skip-build packages the folder as it is.")
    build = args.build_dir.resolve()
    output = args.output.resolve()
    version = args.version or project_version()
    if not re.fullmatch(r"\d+(\.\d+){0,3}", version):
        parser.error(f"The version {version!r} must be numbers separated by dots, such as 1.0 or 1.2.3; "
                     "set it in Player Settings or pass --version.")
    if output == build or output.is_relative_to(build):
        parser.error("--output must be outside the build folder, or the installer would pack itself.")

    compiler = find_inno()  # fail before the long build, not after it
    if not args.skip_build:
        build_executable(build, args.skip_unity, args.include_env)
    check_build(build, args.include_env)

    output.mkdir(parents=True, exist_ok=True)
    print(f"Packing {build} into an installer with {compiler}. This takes a few minutes.", flush=True)
    result = subprocess.call([str(compiler), "/Qp", f"/DBuildDir={build}", f"/DOutputDir={output}",
                              f"/DAppVersion={version}", str(SCRIPT)], cwd=SCRIPT.parent)
    installer = output / f"Formula9-Setup-{version}.exe"
    if result or not installer.is_file():
        raise SystemExit(f"Inno Setup failed (exit code {result}); its errors are above.")
    print(f"\nMade {installer} ({installer.stat().st_size / 2**20:.0f} MB). It installs Formula9 for the current "
          "user without administrator rights; Settings > Apps uninstalls it.", flush=True)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        raise SystemExit(130)
