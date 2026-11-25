-- Author: Nur Syazliyarni Binte Zazali
-- Date Created: 15/11/2026
-- Last Modified: 23/11/2026
-- Pin the grading plan at admission and save the report in the terminal transaction.
CREATE TABLE IF NOT EXISTS session_assessments (
    session_id TEXT PRIMARY KEY REFERENCES sessions(session_id) ON DELETE CASCADE,
    trainee_id TEXT NOT NULL,
    plan_json TEXT NOT NULL,
    result_json TEXT
);
