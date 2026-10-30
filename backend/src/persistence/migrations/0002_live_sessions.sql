-- Private actor state never enters client snapshots. It commits with public state.
CREATE TABLE session_runtimes (
    session_id TEXT PRIMARY KEY REFERENCES sessions(session_id),
    checkpoint_json TEXT NOT NULL,
    state_hash TEXT NOT NULL
);

CREATE TABLE session_creations (
    actor_id TEXT NOT NULL,
    idempotency_key TEXT NOT NULL,
    request_hash TEXT NOT NULL,
    session_id TEXT NOT NULL REFERENCES sessions(session_id),
    PRIMARY KEY (actor_id, idempotency_key)
);
