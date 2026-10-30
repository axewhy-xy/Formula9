# BE-M2-03 persistence handoff

The runtime training database is `data/training.sqlite` by default. Set
`TRAINING_DATABASE_PATH` to a local, writable path to move it. The existing
static scene database remains separate and read-only. The schema is migrated
from numbered SQL files in `backend/src/persistence/migrations`; the database
uses foreign keys, WAL, and `synchronous=FULL`. A process-held lock file
prevents a second backend writer from taking authority over the same file.

## Actor boundary

`TrainingStore.create_session(snapshot)` commits the first validated v2 full
snapshot. The initial `event_sequence` is its replay floor. The actor calls
`accept(authenticated_actor_id, command)` to persist a `QUEUED` command before
acknowledging it. The idempotency scope is actor + session + key. An identical
retry returns the original command and status; a changed payload conflicts.
The command ID is not part of the payload hash, so a retry with a newly
generated ID still reconciles to the original command.

At a simulation boundary, the actor stages a new full snapshot and any domain
events, then calls `apply(command_id, staged_snapshot, domain_events)`. The
writer rechecks the target aggregate revision, epoch, and candidate conditions
against the latest committed snapshot. It atomically writes the domain events,
command outcome event, command status, updated snapshot, and checkpoint. The
caller may publish the returned result only after `apply` succeeds. A stale
command gets one durable `REJECTED` outcome; its staged state is discarded.
`commit_snapshot(snapshot, domain_events)` handles a boundary with no command.
Every published actor boundary must pass through one of these methods.

The checkpoint is canonical v2 full-snapshot JSON plus its SHA-256 hash, tick,
epoch, and included event cursor. Inputs remain in the `commands` table in
writer order; durable events are indexed by session and contiguous sequence.
Replay is paginated and starts at the checkpoint's initial cursor. A client
that asks for an older cursor must fetch a new full snapshot.

## Recovery

Opening an existing database verifies the latest checkpoint hash, increments
the epoch of every nonterminal session, and writes a new checkpoint. Unstarted
`READY` attempts remain `READY` for pre-trip approval and Start; previously
active attempts recover `PAUSED` and require Resume.
Previously `QUEUED` commands become `NEEDS_RECONFIRMATION` with durable events;
they will not apply automatically. Applied/rejected command rows keep their
original outcome and event ID. A new decision requires a fresh epoch, target
revision, command ID, and idempotency key. Downtime does not advance the tick.
An incompatible schema or corrupt checkpoint fails startup instead of exposing
unverified state. Back up the database with SQLite's backup API or while the
server is stopped; copying only the live main `.sqlite` file omits WAL content.

## HTTP surface

- `GET /api/v2/sessions/{id}/snapshot`
- `POST /api/v2/sessions/{id}/commands` → durable `QUEUED` receipt (202)
- `GET /api/v2/sessions/{id}/commands/{idempotency_key}` → current status
- `GET /api/v2/sessions/{id}/events?after=N&limit=100` → ordered replay

These routes take actor identity from trusted Starlette authentication
middleware (`request.scope["user"]`). They reject unauthenticated requests;
the request body and arbitrary headers cannot select an actor. The v2 fleet
actor and production authentication middleware are separate integration work,
so the store is currently exercised through its Python boundary and endpoint
tests. The M1 trajectory endpoints continue to use their existing in-memory
session manager.
