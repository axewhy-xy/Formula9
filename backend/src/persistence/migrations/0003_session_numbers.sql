-- Hands out the running number in each readable session id (toa-payoh-007-10nov-1340).
-- AUTOINCREMENT never reuses a number, even after the newest session is deleted,
-- so a deleted session's id is never given to a new one.
CREATE TABLE session_numbers (
    number INTEGER PRIMARY KEY AUTOINCREMENT,
    issued_at_utc TEXT NOT NULL
);
