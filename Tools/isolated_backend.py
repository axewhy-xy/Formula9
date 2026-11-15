"""
Run this repository's backend for end-to-end tests, isolated from team data.

Author: Choo Jian Wei
Contributors: Lau Xiang Yang (default to this repository)
Date Created: 30/10/2026
Last Modified: 15/11/2026

Description:
    Accounts live in memory (never the shared MongoDB) and training sessions in a temporary
    database, so tests can sign up throwaway users and create sessions freely. It refuses to
    start if a MongoDB URI is still configured.

    Usage (from this repository's root):

        python Tools/isolated_backend.py [--port 8010] [--seed-incidents]
        --formula9 <repository> optionally selects another checkout for compatibility tests.
        --seed-incidents schedules SEEDED_INCIDENTS in every session, beside the random ones, so
        tests that decide incidents have known ones to decide.

    Then run the EndToEnd tests with FORMULA9_E2E_BACKEND_URL=http://127.0.0.1:8010. The incident
    end-to-end test and the walkthrough (FORMULA9_BACKEND_URL) need --seed-incidents.
"""

import argparse
import os
import sys
import tempfile
from pathlib import Path

# The incidents --seed-incidents schedules, in scenario seconds from the start of the integrated
# exercise: a collision on a road shuttle-02 and shuttle-04 both drive (about 450 m ahead of them,
# with a way round it), and a signal fault at a junction about 440 m ahead of shuttle-01, which
# can't be routed round but has a stop before it to hold at.
SEEDED_INCIDENTS = [
    dict(event_id="two-shuttles", event_type="COLLISION", edge_id="road-864872994:0:forward", at_s=8.0, duration_s=400.0),
    dict(event_id="lights", event_type="SIGNAL_FAULT", edge_id=None, node_id="junction-031", at_s=10.0, duration_s=400.0),
]


"""
# -----------------------------------------------------------------------------------
# Function: main
# -----------------------------------------------------------------------------------
Description:
    This function points the backend at a temporary training database, blanks the MongoDB URI (a
    blank value wins over backend/.env), installs an in-memory account store, and serves the app on
    127.0.0.1.

Raises:
    SystemExit: If a MongoDB URI is still configured.
"""
def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--formula9", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--port", type=int, default=8010)
    parser.add_argument("--seed-incidents", action="store_true", help="Schedule SEEDED_INCIDENTS in every session")
    args = parser.parse_args()
    repo = args.formula9.resolve()
    sys.path[:0] = [str(repo / "backend"), str(repo)]
    os.environ["TRAINING_DATABASE_PATH"] = str(Path(tempfile.mkdtemp(prefix="formula9-isolated-")) / "sessions.sqlite")
    os.environ["MONGODB_URI"] = ""  # a blank value wins over backend/.env, which load_dotenv does not override
    os.chdir(repo / "backend")

    from src.core.config import get_settings
    get_settings.cache_clear()
    if get_settings().mongodb_uri:
        raise SystemExit("Refusing to start: a MongoDB URI is configured")

    from src.auth import service as auth_service
    from src.auth.service import AuthService
    from src.auth.store import MemoryAccountStore
    auth_service.set_auth_service(AuthService(MemoryAccountStore()))

    if args.seed_incidents:
        # Every session shares the cached workflow definition's catalogue, so its timeline is theirs
        from src.services.incident_scenarios.world_events import ScheduledEvent
        from src.services.integrated_actor import get_workflow_definition
        get_workflow_definition().incidents.simulated_catalog.timeline = [ScheduledEvent(**row) for row in SEEDED_INCIDENTS]

    import uvicorn
    from src.app import app
    print(f"Isolated backend on http://127.0.0.1:{args.port}: in-memory accounts, "
          f"training database {os.environ['TRAINING_DATABASE_PATH']}"
          f"{', seeded incidents' if args.seed_incidents else ''}", flush=True)
    uvicorn.run(app, host="127.0.0.1", port=args.port, log_level="info")


if __name__ == "__main__":
    main()
