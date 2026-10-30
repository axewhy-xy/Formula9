CREATE TABLE sessions (
    session_id TEXT PRIMARY KEY,
    scenario_revision_id TEXT NOT NULL,
    epoch INTEGER NOT NULL CHECK (epoch >= 0),
    status TEXT NOT NULL,
    tick INTEGER NOT NULL CHECK (tick >= 0),
    event_sequence INTEGER NOT NULL CHECK (event_sequence >= 0),
    base_sequence INTEGER NOT NULL CHECK (base_sequence >= 0),
    snapshot_json TEXT NOT NULL,
    state_hash TEXT NOT NULL
);

CREATE TABLE commands (
    command_id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL REFERENCES sessions(session_id),
    actor_id TEXT NOT NULL,
    idempotency_key TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    command_json TEXT NOT NULL,
    reason TEXT,
    expected_epoch INTEGER NOT NULL,
    expected_revision INTEGER NOT NULL,
    target_type TEXT NOT NULL,
    target_id TEXT NOT NULL,
    target_revision INTEGER NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('QUEUED', 'APPLIED', 'REJECTED', 'NEEDS_RECONFIRMATION', 'EXPIRED')),
    accepted_at_utc TEXT NOT NULL,
    finished_at_utc TEXT,
    applied_tick INTEGER,
    result_event_id TEXT,
    failure_code TEXT,
    UNIQUE (actor_id, session_id, idempotency_key)
);
CREATE INDEX commands_pending ON commands(session_id, status, accepted_at_utc);

CREATE TABLE events (
    session_id TEXT NOT NULL REFERENCES sessions(session_id),
    event_sequence INTEGER NOT NULL,
    event_id TEXT NOT NULL UNIQUE,
    event_json TEXT NOT NULL,
    PRIMARY KEY (session_id, event_sequence)
);

CREATE TABLE checkpoints (
    session_id TEXT NOT NULL REFERENCES sessions(session_id),
    event_sequence INTEGER NOT NULL,
    tick INTEGER NOT NULL,
    epoch INTEGER NOT NULL,
    snapshot_json TEXT NOT NULL,
    state_hash TEXT NOT NULL,
    PRIMARY KEY (session_id, tick, event_sequence)
);
