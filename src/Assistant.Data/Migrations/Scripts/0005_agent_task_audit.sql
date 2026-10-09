-- The activity log of what the Assistant did (PROJECT_SPEC 3.5, 4.8, 4.9, step 117). Migration 1 made task_audit_records for one row for each tool run;
-- this makes it hold what the panel and the activity page show: which run a step belongs to and its place in it, what kind of thing it was, what it was
-- in a few fixed words, what the user answered when asked, and why it did not work. A run of the agent with several steps is a row of its own.
--
-- Nothing here can hold private content: no argument, no result, no prompt, no answer, and no message a tool or a server wrote about its own failure. The
-- summary is fixed words with at most a tidied name in them, the error code is one of a fixed few, and the confirmation is the name of the user's answer.
-- Every enum is stored by name without a CHECK, as in the other tables, so a new value needs no table rebuild.

CREATE TABLE agent_tasks (
    id              TEXT NOT NULL PRIMARY KEY,
    conversation_id TEXT REFERENCES conversations (id) ON DELETE SET NULL,
    status          TEXT NOT NULL,
    failure_point   TEXT,
    started_at      TEXT NOT NULL,
    completed_at    TEXT
) STRICT;

CREATE INDEX ix_agent_tasks_started_at ON agent_tasks (started_at DESC);

-- A step belongs to a run, and goes with it when the run is deleted; an action of its own (an integration that was installed) has no run.
ALTER TABLE task_audit_records ADD COLUMN task_id TEXT REFERENCES agent_tasks (id) ON DELETE CASCADE;
ALTER TABLE task_audit_records ADD COLUMN kind TEXT NOT NULL DEFAULT 'ToolCall';
ALTER TABLE task_audit_records ADD COLUMN step_number INTEGER NOT NULL DEFAULT 0;
ALTER TABLE task_audit_records ADD COLUMN summary TEXT NOT NULL DEFAULT '';
ALTER TABLE task_audit_records ADD COLUMN confirmation TEXT;
ALTER TABLE task_audit_records ADD COLUMN error_code TEXT;

CREATE INDEX ix_task_audit_records_task_id ON task_audit_records (task_id) WHERE task_id IS NOT NULL;
