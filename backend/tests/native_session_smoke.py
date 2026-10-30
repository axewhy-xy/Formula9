"""Run native Create/Join/Start/Pause/Resume against isolated, authenticated HTTP."""

import argparse
from datetime import UTC, datetime, timedelta
import hashlib
import os
from pathlib import Path
import re
import socket
import subprocess
import tempfile
import threading
import time

# The native tests log in with this token (Native/Tests/test_login.h)
NATIVE_TEST_USER = "unit-test"
NATIVE_TEST_TOKEN = "unit-test-token"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, required=True)
    parser.add_argument("--web", action="store_true", help="Also run the real web API client against the native-created session")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    temporary = root / ".tools" / "session-smoke"
    temporary.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=temporary) as directory:
        os.environ["TRAINING_DATABASE_PATH"] = str(Path(directory) / "sessions.sqlite")
        from src.core.config import get_settings
        get_settings.cache_clear()
        # Accept the native test login from an in-memory account store, so no MongoDB is needed
        from src.auth import service as auth_service
        from src.auth.service import AuthService
        from src.auth.store import MemoryAccountStore
        accounts = MemoryAccountStore()
        now = datetime.now(UTC)
        accounts.add_session({"token_hash": hashlib.sha256(NATIVE_TEST_TOKEN.encode()).hexdigest(),
                              "username": NATIVE_TEST_USER, "created_at": now, "expires_at": now + timedelta(hours=1)})
        auth_service.set_auth_service(AuthService(accounts))
        import uvicorn
        from src.app import app
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            listener.listen()
            port = listener.getsockname()[1]
            server = uvicorn.Server(uvicorn.Config(app, log_level="warning"))
            thread = threading.Thread(target=server.run, kwargs={"sockets": [listener]}, daemon=True)
            thread.start()
            try:
                deadline = time.monotonic() + 10
                while not server.started and thread.is_alive() and time.monotonic() < deadline:
                    time.sleep(0.02)
                if not server.started:
                    raise RuntimeError("Training backend did not start")
                environment = {**os.environ, "FORMULA9_BACKEND_URL": f"http://127.0.0.1:{port}",
                               "FORMULA9_LIVE_TRAINING_SMOKE": "1", "QT_QPA_PLATFORM": "offscreen"}
                result = subprocess.run([str(args.executable.resolve()),
                                "--gtest_filter=BackendClientTrainingIntegrationTest.*"],
                               env=environment, capture_output=True, text=True, timeout=30)
                print(result.stdout)
                if result.returncode:
                    print(result.stderr)
                    result.check_returncode()
                if args.web:
                    match = re.search(r"TRAINING_SMOKE_SESSION_ID=([A-Za-z0-9-]+)", result.stdout)
                    if match is None:
                        raise RuntimeError("Native smoke did not report its session")
                    environment.update({
                        "VITE_TRAINING_SMOKE_BASE_URL": f"http://127.0.0.1:{port}",
                        "VITE_TRAINING_SMOKE_SESSION_ID": match.group(1),
                        "VITE_TRAINING_SMOKE_TOKEN": NATIVE_TEST_TOKEN,
                        "TEMP": directory, "TMP": directory,
                    })
                    subprocess.run(["npm.cmd" if os.name == "nt" else "npm", "exec", "--", "vitest", "run",
                                    "src/api/training_session_live.test.ts", "--reporter=dot"],
                                   cwd=root / "frontend", env=environment, check=True, timeout=30)
            finally:
                server.should_exit = True
                thread.join(10)


if __name__ == "__main__":
    main()
