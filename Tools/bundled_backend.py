"""
Serve the backend that ships beside the Windows player.

Author: Lau Xiang Yang
Date Created: 06/11/2026
Last Modified: 10/11/2026

Description:
    Tools/build_executable.py copies this file into the build as Backend/serve.py, next to the
    portable Python, backend/ and contracts/ it serves. Every Formula9.exe that opens runs it hidden
    (BundledBackend.cs) and passes its own process ID. All open players share one backend:

    - The first player's serve.py becomes the backend. Later players' serve.py add their player to
      Backend/state/backend.json and exit at once, using the backend that is already running.
    - The backend checks that file every second and shuts down cleanly (so SQLite folds its WAL
      back into the database) once every listed player has closed, even if one crashed. If a clean
      shutdown takes longer than ten seconds, it ends itself.
    - A player that opens while the backend is shutting down starts the next one, which waits for
      the old one to exit first.
    - If something else already serves the port (for example run.bat's backend), it is used instead.

    backend.json is only read or written while holding Backend/state/backend.lock, so a player
    cannot join a backend that has already decided to stop.

        python serve.py [--host 127.0.0.1] [--port 8000] [--parent-pid PID] [--log FILE] [--state DIR]

    It also works from the repository with backend/.venv's Python, for checking it without a build.
"""

from __future__ import annotations

import argparse
from contextlib import contextmanager
from datetime import datetime
import json
import os
from pathlib import Path
import socket
import sys
import threading
import time
from typing import Iterator

HERE = Path(__file__).resolve().parent
# In a build this file sits beside backend/; in the repository it is in Tools/.
ROOT = HERE if (HERE / "backend").is_dir() else HERE.parent
CHECK_SECONDS = 1.0
SHUTDOWN_SECONDS = 10
PREVIOUS_BACKEND_SECONDS = 30
LOCK_SECONDS = 15
LOG_LIMIT = 5 * 2**20

if os.name == "nt":
    import ctypes
    from ctypes import wintypes
    import msvcrt

    _kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    _kernel32.OpenProcess.restype = wintypes.HANDLE
    _kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    _kernel32.WaitForSingleObject.restype = wintypes.DWORD
    _kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    _kernel32.GetProcessTimes.restype = wintypes.BOOL
    _kernel32.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(ctypes.c_ulonglong)] * 4
    _kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    _SYNCHRONIZE, _QUERY_LIMITED_INFORMATION, _WAIT_TIMEOUT = 0x00100000, 0x1000, 0x102
else:
    import fcntl


"""
# -----------------------------------------------------------------------------------
# Function: open_process
# -----------------------------------------------------------------------------------
Description:
    This function opens a Windows process handle that can be waited on and asked for its start
    time. The caller closes it.

Args:
    pid (int): The process.

Returns:
    The handle, or None if the process is gone or cannot be opened.
"""
def open_process(pid: int):
    return _kernel32.OpenProcess(_SYNCHRONIZE | _QUERY_LIMITED_INFORMATION, False, pid) or None


"""
# -----------------------------------------------------------------------------------
# Function: identify
# -----------------------------------------------------------------------------------
Description:
    This function names a running process by its ID and start time. Windows reuses process IDs, so
    the start time is what tells a closed player from a new, unrelated process with the same ID.

Args:
    pid (int): The process.

Returns:
    dict | None: {"pid", "started"}, or None if the process is not running.
"""
def identify(pid: int) -> dict | None:
    if os.name != "nt":
        try:
            os.kill(pid, 0)
        except PermissionError:
            pass
        except OSError:
            return None
        return {"pid": pid, "started": 0}
    handle = open_process(pid)
    if handle is None:
        return None
    try:
        if _kernel32.WaitForSingleObject(handle, 0) != _WAIT_TIMEOUT:
            return None  # exited, but something still holds it open
        started, unused = ctypes.c_ulonglong(), ctypes.c_ulonglong()
        if not _kernel32.GetProcessTimes(handle, ctypes.byref(started), ctypes.byref(unused),
                                         ctypes.byref(unused), ctypes.byref(unused)):
            return None
        return {"pid": pid, "started": started.value}
    finally:
        _kernel32.CloseHandle(handle)


"""
# -----------------------------------------------------------------------------------
# Function: is_running
# -----------------------------------------------------------------------------------
Description:
    This function checks that the process identify() named is still the one running.

Args:
    process (dict | None): A result of identify().

Returns:
    bool: True if that same process is still running.
"""
def is_running(process: dict | None) -> bool:
    return bool(process) and identify(process["pid"]) == {"pid": process["pid"], "started": process["started"]}


"""
# -----------------------------------------------------------------------------------
# Function: wait_for_exit
# -----------------------------------------------------------------------------------
Description:
    This function blocks until a process identify() named has exited, or until the timeout passes.

Args:
    process (dict): A result of identify().
    seconds (float): How long to wait at most.
"""
def wait_for_exit(process: dict, seconds: float) -> None:
    deadline = time.monotonic() + seconds
    while is_running(process) and time.monotonic() < deadline:
        time.sleep(0.2)


"""
# -----------------------------------------------------------------------------------
# Function: registry_lock
# -----------------------------------------------------------------------------------
Description:
    This function holds state/backend.lock while backend.json is read or changed, so every serve.py
    sees the others' changes. The operating system releases it if its holder crashes.

Args:
    state (Path): The state folder.

Raises:
    TimeoutError: If another process held it for LOCK_SECONDS.
"""
@contextmanager
def registry_lock(state: Path) -> Iterator[None]:
    handle = open(state / "backend.lock", "a+b")
    try:
        deadline = time.monotonic() + LOCK_SECONDS
        while True:
            handle.seek(0)
            try:
                if os.name == "nt":
                    msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except OSError:
                if time.monotonic() > deadline:
                    raise TimeoutError(f"{state / 'backend.lock'} stayed locked for {LOCK_SECONDS} seconds")
                time.sleep(0.05)
        try:
            yield
        finally:
            handle.seek(0)
            if os.name == "nt":
                msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(handle.fileno(), fcntl.LOCK_UN)
    finally:
        handle.close()


"""
# -----------------------------------------------------------------------------------
# Function: read_registry
# -----------------------------------------------------------------------------------
Description:
    This function reads backend.json, dropping players that have closed. Hold registry_lock.

Args:
    state (Path): The state folder.

Returns:
    dict: {"owner": the serving process or None, with "stopping"; "players": the open players}.
"""
def read_registry(state: Path) -> dict:
    try:
        registry = json.loads((state / "backend.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        registry = {}
    players = registry.get("players") if isinstance(registry, dict) else None
    owner = registry.get("owner") if isinstance(registry, dict) else None
    return {"owner": owner if isinstance(owner, dict) else None,
            "players": [player for player in players or [] if isinstance(player, dict) and is_running(player)]}


"""
# -----------------------------------------------------------------------------------
# Function: write_registry
# -----------------------------------------------------------------------------------
Description:
    This function replaces backend.json in one step, so a crash never leaves half a file. Hold
    registry_lock.

Args:
    state (Path): The state folder.
    registry (dict): What read_registry() returned, changed.
"""
def write_registry(state: Path, registry: dict) -> None:
    partial = state / "backend.json.partial"
    partial.write_text(json.dumps(registry, indent=2), encoding="utf-8")
    os.replace(partial, state / "backend.json")


"""
# -----------------------------------------------------------------------------------
# Function: join_or_own
# -----------------------------------------------------------------------------------
Description:
    This function adds the player to the registry, then either finds a backend that is serving and
    not stopping, or makes this process the backend.

Args:
    state (Path): The state folder.
    player (dict): The player, from identify().

Returns:
    tuple[dict | None, dict | None]: (the backend to use, or None if this process serves; the
        backend that is still shutting down, which this one has to wait for, or None).
"""
def join_or_own(state: Path, player: dict) -> tuple[dict | None, dict | None]:
    with registry_lock(state):
        registry = read_registry(state)
        registry["players"] = [entry for entry in registry["players"] if entry != player] + [player]
        owner = registry["owner"]
        if is_running(owner) and not owner.get("stopping"):
            write_registry(state, registry)
            return owner, None
        registry["owner"] = {**identify(os.getpid()), "stopping": False}
        write_registry(state, registry)
        return None, owner if is_running(owner) else None


"""
# -----------------------------------------------------------------------------------
# Function: give_up_ownership
# -----------------------------------------------------------------------------------
Description:
    This function removes this process as the backend, unless a newer one has already replaced it.

Args:
    state (Path): The state folder.
"""
def give_up_ownership(state: Path) -> None:
    try:
        with registry_lock(state):
            registry = read_registry(state)
            if registry["owner"] and registry["owner"].get("pid") == os.getpid():
                registry["owner"] = None
                write_registry(state, registry)
    except (OSError, TimeoutError) as error:
        print(f"Could not update {state / 'backend.json'}: {error}", flush=True)


"""
# -----------------------------------------------------------------------------------
# Function: stop_when_players_close
# -----------------------------------------------------------------------------------
Description:
    This function starts a thread that checks the registry every second. Once no listed player is
    open, it marks this backend as stopping, so the next player starts a new one, and asks the
    server to shut down; it ends the process outright if that takes longer than SHUTDOWN_SECONDS.

Args:
    server: The uvicorn server to stop.
    state (Path): The state folder.
"""
def stop_when_players_close(server, state: Path) -> None:
    def watch() -> None:
        while True:
            time.sleep(CHECK_SECONDS)
            try:
                with registry_lock(state):
                    registry = read_registry(state)
                    if not registry["players"] and registry["owner"] and registry["owner"].get("pid") == os.getpid():
                        registry["owner"]["stopping"] = True
                    write_registry(state, registry)
                    if not registry["players"]:
                        break
            except (OSError, TimeoutError) as error:
                print(f"Could not check which players are open: {error}", flush=True)
        print("Every Formula9 window has closed; stopping the backend.", flush=True)
        server.should_exit = True
        time.sleep(SHUTDOWN_SECONDS)
        print(f"The backend did not stop within {SHUTDOWN_SECONDS} seconds; ending it.", flush=True)
        os._exit(1)

    threading.Thread(target=watch, name="player-watch", daemon=True).start()


"""
# -----------------------------------------------------------------------------------
# Function: is_listening
# -----------------------------------------------------------------------------------
Description:
    This function checks whether something accepts connections on host:port.

Args:
    host (str): The host to try.
    port (int): The port to try.

Returns:
    bool: True if a connection was accepted within half a second.
"""
def is_listening(host: str, port: int) -> bool:
    try:
        with socket.create_connection((host, port), timeout=0.5):
            return True
    except OSError:
        return False


"""
# -----------------------------------------------------------------------------------
# Function: send_output_to
# -----------------------------------------------------------------------------------
Description:
    This function appends all further output to a log file, so one player's start never erases
    what another's backend wrote. The backend that is starting moves a log over LOG_LIMIT to
    <name>.1 first, if no other process still has it open.

Args:
    log (Path): The log file.
    rotate (bool): Whether to move a large log aside first.
"""
def send_output_to(log: Path, rotate: bool) -> None:
    log.parent.mkdir(parents=True, exist_ok=True)
    if rotate:
        try:
            if log.stat().st_size > LOG_LIMIT:
                os.replace(log, log.with_name(log.name + ".1"))
        except OSError:
            pass
    sys.stdout = sys.stderr = open(log, "a", encoding="utf-8", buffering=1)


"""
# -----------------------------------------------------------------------------------
# Function: serve
# -----------------------------------------------------------------------------------
Description:
    This function points Python at the bundled backend and contracts and serves the API until it
    is stopped.

Args:
    args: The parsed command line.
    state (Path | None): The state folder to watch for open players, or None to serve until stopped.

Returns:
    int: 0 after a clean shutdown, 1 if the server could not start.
"""
def serve(args, state: Path | None) -> int:
    # The same layout run_backend.py serves: backend/ owns `src`, the root owns `contracts`.
    sys.path[:0] = [str(ROOT / "backend"), str(ROOT)]
    os.chdir(ROOT)
    print(f"Backend: {ROOT}\nPython: {sys.executable}\nServing http://{args.host}:{args.port}", flush=True)

    import uvicorn
    server = uvicorn.Server(uvicorn.Config("src.app:app", host=args.host, port=args.port, log_level="info"))
    if state is not None:
        stop_when_players_close(server, state)
    server.run()
    return 0 if server.started else 1


"""
# -----------------------------------------------------------------------------------
# Function: main
# -----------------------------------------------------------------------------------
Description:
    This function joins the backend the other open players use, or becomes it, and serves the API
    until every player has closed. Without --parent-pid it simply serves until stopped.

Returns:
    int: 0 after joining a backend or a clean shutdown, 1 if the server could not start.
"""
def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8000)
    parser.add_argument("--parent-pid", type=int, help="Share the backend with this player and stop once it and "
                                                       "every other player using it have closed")
    parser.add_argument("--log", type=Path, help="Append all output to this file instead of the console")
    parser.add_argument("--state", type=Path, default=ROOT / "state",
                        help="Folder for the record of open players (default: state beside backend/)")
    args = parser.parse_args()
    stamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")

    if not args.parent_pid:
        if args.log:
            send_output_to(args.log, rotate=True)
        print(f"==== {stamp}: serve.py (process {os.getpid()}) started", flush=True)
        return serve(args, None)

    player = identify(args.parent_pid)
    if player is None:
        return 0  # the player closed before its backend could start
    state = args.state.resolve()
    state.mkdir(parents=True, exist_ok=True)
    try:
        backend, previous = join_or_own(state, player)
    except (OSError, TimeoutError) as error:
        if args.log:
            send_output_to(args.log, rotate=False)
        print(f"==== {stamp}: Formula9 (process {player['pid']}) could not reach the backend record: {error}",
              flush=True)
        return 1
    if args.log:
        send_output_to(args.log, rotate=backend is None)
    if backend is not None:
        print(f"==== {stamp}: Formula9 (process {player['pid']}) is using the backend already running "
              f"in process {backend['pid']}.", flush=True)
        return 0

    print(f"==== {stamp}: Formula9 (process {player['pid']}) started this backend "
          f"(process {os.getpid()}).", flush=True)
    try:
        if previous is not None:
            print(f"Waiting for the previous backend (process {previous['pid']}) to finish shutting down.", flush=True)
            wait_for_exit(previous, PREVIOUS_BACKEND_SECONDS)
        if is_listening(args.host, args.port):
            print(f"Something else is already serving {args.host}:{args.port}, such as run.bat's backend; "
                  "the players will use it.", flush=True)
            return 0
        return serve(args, state)
    finally:
        give_up_ownership(state)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        raise SystemExit(130)
