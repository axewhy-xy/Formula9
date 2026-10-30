# Backend migration to Formula9

The FastAPI backend and its shared Python contracts now live alongside the
Unity client. Normal startup, contract generation, backend tests and Unity
end-to-end tests use files in this repository. A sibling `Formula_9` checkout
is no longer required.

The migration source was the clean local checkout of
`https://github.com/axewhy-xy/Formula_9` at commit
`a5d699ec8c4536f7b52c248e4bd7140b1a415117`. The GitHub URL returned a repository
redirect during migration. [Source hashes](backend-migration-source.json)
record every copied file before the path and documentation adaptations below.

## Included

- The entire `backend/src`, SQL migrations, fixtures, dependency locks and test
  suite: authentication, legacy HTTP/WebSocket playback, maps and routing,
  fleet simulation, persistence/recovery, energy, charging, incidents, spatial
  scenes/motion, instrumentation, dataset adapters and optional perception.
- All shared contracts, schemas, generators and fixtures under `contracts`.
- The static SQLite/OSM map, relocated from the Qt asset folder to
  `backend/fixtures/map`. Scene assets remain under `backend/fixtures/m2_scene`.
- Python support scripts for scene generation, KITTI downloads/exports,
  visualization and optional ML setup. The old Qt/web bootstrap is superseded
  by this repository's backend launcher.
- Backend session documentation, M2 backend handoffs and committed numeric
  evidence needed by the backend tests. These preserve historical Qt/web
  references; use the root README for current Unity startup.

## Repository adaptations

- Default static map: `backend/fixtures/map/map.db`.
- Default live session database: `state/training.sqlite`, ignored by Git.
- `Tools/generate_contracts.py` reads local `contracts/v2`, copies the local
  scene package and preserves existing Unity `.meta` files.
- `Tools/isolated_backend.py` defaults to this repository; its `--formula9`
  override remains available for comparison with another checkout.
- `run.bat` / `Tools/run_backend.py` offer setup, backend tests and server
  startup. Reload watches backend source and contracts rather than Unity's
  generated folders. Tests use temporary storage and disable MongoDB.
- The hash-pinned scene manifest and static database retain their exact bytes
  across Git checkouts.

The backend remains Python/FastAPI; Unity remains its HTTP client. The public
API and simulation algorithms are preserved. This migration does not rewrite
the server into Unity C#.

## Later source changes

Commit `7fdccb4` of the source repository, the real Singapore street fixture and LTA transit
backend, was ported after the migration; its Qt map work became the Unity `FleetMap`,
`MapGeo`, `MapTiles` and `TransitClient`. `backend-migration-source.json` still records the
migration commit, so its hashes for the files that commit changed no longer match.

## Local data and accounts

Private `.env` files, virtual environments, generated caches, downloaded KITTI
data/models and live session databases were excluded. Copy
`backend/.env.example` to `backend/.env` and configure `MONGODB_URI` privately.
Pointing it at the existing account database preserves access to those accounts.
The server starts without MongoDB, but authentication returns 503 until an
account store is configured. The isolated test server uses in-memory accounts.

Existing durable sessions can be retained by setting `TRAINING_DATABASE_PATH`
to an existing database after stopping the old server. A live database is not
automatically copied or opened during migration. Optional datasets can be
located through `KITTI_DATA_ROOT` or downloaded with `scripts/download_kitti.py`.

The legacy `backend/tests/native_*_smoke.py` and `web_m2_05_demo.py` helpers are
retained as source. Running them requires the corresponding old client/build;
they are not part of the backend pytest suite or the Unity test workflow.

## Migration validation

- Backend: **869 passed, 4 skipped**. The skipped tests require opt-in live
  OpenStreetMap downloads. The run included real process crash/recovery,
  repository path checks, persistence and the integrated live workflows.
- Unity: **109 passed, 2 skipped** against this repository's disposable backend
  on port 8012, including the rendered scene and route/charging/incident flows.
  The two optional tests expected a separate backend on the default port 8000.
- Live launcher smoke check from outside the repository root: `/api/health`,
  `/api/scenes/default` and `/docs` returned HTTP 200. The bundled static map
  contained 345 roads, 322 buildings and 94 POIs.
- Local contract generation succeeded (43 classes from 13 schemas), preserved
  Unity metadata, and the regenerated contracts passed the Unity suite.
- Copied source hashes match the original files except for the documented
  adaptations to `backend/src/core/config.py`, `backend/.env.example` and
  `backend/README.md`.

The full backend validation initially used Python 3.14.4 with an isolated
verification environment and installed dependencies. A follow-up installed the
unchanged dependency lock into this repository's own `backend/.venv` using
Python 3.12.10: `pip check` succeeded and 49 focused launcher, map/API and session
creation tests passed.

The launcher now requires that local environment, discards inherited Python
import paths, and prints its repository/interpreter/backend paths at startup.
`run.bat` changes to its own repository directory before starting. Startup and
map/health requests passed with old-checkout environment variables injected,
and backend/map loading also passed with file reads from `Formula_9` blocked
by a Python audit hook. The old project directory is not a runtime dependency.
Temporary servers used disposable accounts/storage and were stopped afterward.
