-- What the next publication of a session must follow (training_store._boundary): the snapshot ID,
-- epoch, event sequence, tick and the scene, transform and scenario revisions, with the state_hash of
-- the snapshot it was taken from. A commit checks this small JSON instead of parsing the whole stored
-- snapshot. NULL on rows written before this column, and ignored when its state_hash does not match
-- the row's: those rows are read in full once.
ALTER TABLE sessions ADD COLUMN boundary_json TEXT;
