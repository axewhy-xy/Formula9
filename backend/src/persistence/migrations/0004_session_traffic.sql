-- An integrated session's background traffic, checkpointed apart from its other private state:
-- some 100 KB, written about once a second rather than with every publication (see
-- IntegratedSessionActor.traffic_checkpoint). tick is the session tick it was taken at.
CREATE TABLE session_traffic (
    session_id TEXT PRIMARY KEY REFERENCES sessions(session_id),
    tick INTEGER NOT NULL,
    traffic_json TEXT NOT NULL,
    state_hash TEXT NOT NULL
);
