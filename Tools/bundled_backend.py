"""
Serve the backend that ships beside the Windows player.

Author: Lau Xiang Yang
Date Created: 06/11/2026
Last Modified: 06/11/2026

Description:
    Tools/build_executable.py copies this file into the build as Backend/serve.py, next to the
    portable Python, backend/ and contracts/ it serves. Formula9.exe starts it hidden when the
    player opens (BundledBackend.cs) and passes its own process ID, so the server shuts down when
    the player closes, even if the player crashes.

        python serve.py [--host 127.0.0.1] [--port 8000] [--parent-pid PID] [--log FILE]

    It also works from the repository with backend/.venv's Python, for checking it without a build.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import sys
import threading
import time

HERE = Path(__file__).resolve().parent
# In a build this file sits beside backend/; in the repository it is in Tools/.
ROOT = HERE if (HERE / "backend").is_dir() else HERE.parent


"""
# -----------------------------------------------------------------------------------
# Function: wait_for_exit
# -----------------------------------------------------------------------------------
Description:
    This function blocks until the process with the given ID has exited, or returns at once if it
    is already gone.

Args:
    pid (int): The process to wait for.
"""
def wait_for_exit(pid: int) -> None:
    if os.name == "nt":
        import ctypes
        synchronize, infinite = 0x00100000, 0xFFFFFFFF
        kernel32 = ctypes.windll.kernel32
        handle = kernel32.OpenProcess(synchronize, False, pid)
        if not handle:
            return
        try:
            kernel32.WaitForSingleObject(handle, infinite)
        finally:
            kernel32.CloseHandle(handle)
        return
    while True:
        try:
            os.kill(pid, 0)
        except OSError:
            return
        time.sleep(1)


"""
# -----------------------------------------------------------------------------------
# Function: stop_with_parent
# -----------------------------------------------------------------------------------
Description:
    This function starts a thread that asks the server to shut down once the parent process has
    exited, and ends the process outright if a clean shutdown takes longer than ten seconds.

Args:
    server: The uvicorn server to stop.
    pid (int): The parent process (Formula9.exe).
"""
def stop_with_parent(server, pid: int) -> None:
    def watch() -> None:
        wait_for_exit(pid)
        print(f"Formula9 (process {pid}) has closed; stopping the backend.", flush=True)
        server.should_exit = True
        time.sleep(10)
        os._exit(0)

    threading.Thread(target=watch, name="parent-watch", daemon=True).start()


"""
# -----------------------------------------------------------------------------------
# Function: main
# -----------------------------------------------------------------------------------
Description:
    This function points Python at the bundled backend and contracts, optionally sends all output
    to a log file, and serves the API until stopped or until the parent process exits.

Returns:
    int: 0 after a clean shutdown, 1 if the server could not start.
"""
def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8000)
    parser.add_argument("--parent-pid", type=int, help="Stop when this process exits")
    parser.add_argument("--log", type=Path, help="Write all output to this file instead of the console")
    args = parser.parse_args()

    if args.log:
        args.log.parent.mkdir(parents=True, exist_ok=True)
        log = open(args.log, "w", encoding="utf-8", buffering=1)
        sys.stdout = sys.stderr = log

    # The same layout run_backend.py serves: backend/ owns `src`, the root owns `contracts`.
    sys.path[:0] = [str(ROOT / "backend"), str(ROOT)]
    os.chdir(ROOT)
    print(f"Backend: {ROOT}\nPython: {sys.executable}\nServing http://{args.host}:{args.port}", flush=True)

    import uvicorn
    server = uvicorn.Server(uvicorn.Config("src.app:app", host=args.host, port=args.port, log_level="info"))
    if args.parent_pid:
        stop_with_parent(server, args.parent_pid)
    server.run()
    return 0 if server.started else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        raise SystemExit(130)
