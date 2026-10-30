"""Run this repository's backend for end-to-end tests, isolated from team data.

Accounts live in memory (never the shared MongoDB) and training sessions in a temporary
database, so tests can sign up throwaway users and create sessions freely.

Usage (from this repository's root):
    python Tools/isolated_backend.py [--port 8010]
    --formula9 <repository> optionally selects another checkout for compatibility tests.
Then run the EndToEnd tests with FORMULA9_E2E_BACKEND_URL=http://127.0.0.1:8010.
"""

import argparse
import os
import sys
import tempfile
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--formula9", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--port", type=int, default=8010)
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

    import uvicorn
    from src.app import app
    print(f"Isolated backend on http://127.0.0.1:{args.port}: in-memory accounts, "
          f"training database {os.environ['TRAINING_DATABASE_PATH']}", flush=True)
    uvicorn.run(app, host="127.0.0.1", port=args.port, log_level="info")


if __name__ == "__main__":
    main()
