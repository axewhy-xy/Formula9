"""
Run the backend for a showcase: one session that shows every feature built so far within minutes.

Author: Lau Xiang Yang
Date Created: 24/11/2026
Last Modified: 24/11/2026

Description:
    Serves this repository's backend like Tools/isolated_backend.py (in-memory accounts, a fresh
    temporary session database, never the team's MongoDB), with a ready account to log in with and
    the showcase's world events (backend/fixtures/incidents/showcase.json) scheduled in every session
    of the integrated exercise, beside random events that come sooner than usual. docs/showcase.md is
    the walkthrough to follow.

    Usage (from this repository's root, or double-click showcase.bat):

        backend\\.venv\\Scripts\\python Tools/showcase_backend.py [--port 8000]

    It serves on port 8000 by default, the Unity client's default, so Play mode and a built player
    both reach it without FORMULA9_BACKEND_URL. Stop run.bat's backend first: both use that port.
"""

import argparse
import json
import os
import socket
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
SHOWCASE_FIXTURE = REPO / "backend" / "fixtures" / "incidents" / "showcase.json"
# The showcase's own account. It only ever exists in this process's memory.
SHOWCASE_USERNAME = "showcase"
SHOWCASE_PASSWORD = "showcase-demo"


"""
# -----------------------------------------------------------------------------------
# Function: install_showcase
# -----------------------------------------------------------------------------------
Description:
    This function schedules the showcase's world events in every integrated session and brings its
    random events forward, on the workflow definition all those sessions share. The timeline is
    checked against the operating network's roads and the signalled junctions first, so a bad
    fixture fails here rather than in a running session.

Parameters:
    definition: The integrated exercise's WorkflowDefinition (get_workflow_definition()).
    path: The showcase fixture.

Returns:
    The scheduled events, in timeline order.

Raises:
    ValueError: If the fixture's timeline or random policy is invalid.
"""
def install_showcase(definition, path: Path = SHOWCASE_FIXTURE):
    from src.services.incident_scenarios.world_events import load_timeline

    data = json.loads(Path(path).read_text(encoding="utf-8"))
    if data.get("schema_version") != 1:
        raise ValueError("Unsupported showcase schema version")
    timeline = load_timeline(data["timeline"], edges=definition.network["edges"], junctions=definition.signal_junctions)
    catalog = definition.incidents.simulated_catalog
    policy = {**catalog.policy, **data.get("random_generation", {})}
    if not 0 < policy["min_interval_s"] <= policy["max_interval_s"]:
        raise ValueError("Invalid showcase random incident interval")
    catalog.timeline = timeline
    catalog.policy = policy
    return timeline


"""
# -----------------------------------------------------------------------------------
# Function: port_free
# -----------------------------------------------------------------------------------
Description:
    This function reports whether nothing is listening on a local port, so a second backend is
    refused with a plain message rather than a socket error.
"""
def port_free(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        return probe.connect_ex(("127.0.0.1", port)) != 0


"""
# -----------------------------------------------------------------------------------
# Function: main
# -----------------------------------------------------------------------------------
Description:
    This function isolates the backend, adds the showcase account, installs the showcase's events
    and serves the app on 127.0.0.1.

Raises:
    SystemExit: If the port is taken or a MongoDB URI is still configured.
"""
def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--port", type=int, default=8000)
    args = parser.parse_args()
    if not port_free(args.port):
        raise SystemExit(f"Port {args.port} is already in use. Close run.bat's backend (or the other "
                         f"program on it) and start the showcase again.")

    # Sessions tick in this process: worker processes are spawned afresh and would never see the
    # showcase's timeline, which is installed here. One showcase session needs no workers.
    os.environ["F9_SESSION_WORKERS"] = "0"
    sys.path.insert(0, str(REPO))
    from Tools.isolated_backend import isolate
    accounts = isolate(REPO)
    accounts.sign_up(SHOWCASE_USERNAME, SHOWCASE_PASSWORD)

    from src.services.integrated_actor import get_workflow_definition
    timeline = install_showcase(get_workflow_definition())

    import uvicorn
    from src.app import app
    print(f"Showcase backend on http://127.0.0.1:{args.port}", flush=True)
    print(f"  Log in as {SHOWCASE_USERNAME!r} with password {SHOWCASE_PASSWORD!r} (or create an account).")
    print("  Scheduled after Start: " + ", ".join(f"{e.event_type.lower()} at {e.at_s:g} s" for e in timeline))
    print("  Accounts and sessions are forgotten when this window closes. Walkthrough: docs/showcase.md", flush=True)
    uvicorn.run(app, host="127.0.0.1", port=args.port, log_level="warning")


if __name__ == "__main__":
    main()
