-- A name people can give a session when they create it, shown in the session list.
-- Display text only: session_id stays the key, so names need not be unique. NULL for none.
ALTER TABLE sessions ADD COLUMN name TEXT;
