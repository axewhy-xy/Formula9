"""Run the offscreen native editor against the real backend and committed M2 graph.

python -m backend.tests.native_route_smoke --executable build/Native/Tests/Release/native_unit_tests.exe
Only this local test harness overrides authentication and seeds READY sessions.
"""
from __future__ import annotations

import argparse
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
from threading import Thread
import time

import uvicorn

# Match the backend application's documented PYTHONPATH=backend launch convention.
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from backend.tests.test_route_edit import initial
from src.api.routes.training_v2 import get_actor_id, get_training_store
from src.app import app
from src.persistence.training_store import TrainingStore


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, required=True)
    parser.add_argument("--screenshot", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    with tempfile.TemporaryDirectory(prefix="native-route-smoke-", dir=root / "build") as temp:
        with TrainingStore(Path(temp) / "training.sqlite") as store:
            for session_id in ("session-numeric", "session-mouse"):
                snapshot = initial()
                snapshot.session_id = snapshot.session.session_id = session_id
                store.create_session(snapshot)
            app.dependency_overrides[get_training_store] = lambda: store
            app.dependency_overrides[get_actor_id] = lambda: "native-smoke-actor"
            listener = socket.socket()
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
            server = uvicorn.Server(uvicorn.Config(app, log_level="warning", lifespan="off"))
            thread = Thread(target=server.run, kwargs={"sockets": [listener]}, daemon=True)
            thread.start()
            try:
                deadline = time.monotonic() + 10
                while not server.started and time.monotonic() < deadline:
                    time.sleep(0.02)
                if not server.started:
                    raise RuntimeError("Local fixture backend did not start")
                env = dict(os.environ, FORMULA9_ROUTE_SMOKE="1", FORMULA9_BACKEND_URL=f"http://127.0.0.1:{port}")
                if args.screenshot:
                    env["FORMULA9_ROUTE_SCREENSHOT"] = str(args.screenshot.resolve())
                subprocess.run([str(args.executable.resolve()), "--gtest_filter=TrainingRouteIntegrationTest.*"],
                               env=env, check=True, timeout=45)
                for session_id in ("session-numeric", "session-mouse"):
                    committed = store.snapshot(session_id)
                    assert committed.routes[0].revision == 2
                    assert all(vehicle.route_revision == 2 for vehicle in committed.vehicles)
                    assert len([event for event in store.replay(session_id, 0).events if event.event_type == "ROUTE_APPROVED"]) == 1
                print("Both native input methods committed one detour and all five shuttle assignments through the real backend.")
            finally:
                server.should_exit = True
                thread.join(timeout=10)
                listener.close()
                app.dependency_overrides.clear()


if __name__ == "__main__":
    main()
