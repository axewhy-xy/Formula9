-- Author: Nur Syazliyarni Binte Zazali
-- Date Created: 23/11/2026
-- Last Modified: 23/11/2026
-- Retain wall-clock activity across recovery so abandoned attempts release capacity.
ALTER TABLE session_assessments ADD COLUMN activity_at_utc TEXT NOT NULL DEFAULT '';
UPDATE session_assessments SET activity_at_utc = strftime('%Y-%m-%dT%H:%M:%f+00:00', 'now');
