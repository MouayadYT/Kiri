-- The first schema: conversations, messages, settings metadata, people, tool permissions and task audit records.
--
-- Conventions for every table:
--   * A Guid is stored as lowercase "D"-format text.
--   * A moment in time is UTC ISO-8601 text with seven fractional digits ("2026-09-30T10:15:00.0000000Z"), so text
--     order is time order.
--   * An enum is stored by name (as Core's JSON does), not by number, and has no CHECK: a new enum value must not
--     need a table rebuild.
--   * Tables are STRICT, so a column holds only its declared type, and there are no BLOB columns: model files,
--     screenshots and captured content are never stored here (PROJECT_SPEC 3.5, P7).
--   * Rows that belong to another row are deleted with it (ON DELETE CASCADE), so deleting a conversation leaves
--     nothing of it behind.

CREATE TABLE conversations (
    id         TEXT NOT NULL PRIMARY KEY,
    title      TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
) STRICT;

-- The history list (newest first) and the retention cut-off.
CREATE INDEX ix_conversations_updated_at ON conversations (updated_at DESC);

CREATE TABLE messages (
    id              TEXT    NOT NULL PRIMARY KEY,
    conversation_id TEXT    NOT NULL REFERENCES conversations (id) ON DELETE CASCADE,
    position        INTEGER NOT NULL CHECK (position >= 0),
    role            TEXT    NOT NULL,
    text            TEXT    NOT NULL,
    created_at      TEXT    NOT NULL,
    UNIQUE (conversation_id, position)
) STRICT;

-- What a message was asked about: the descriptor of each context item, never its content (PROJECT_SPEC 3.5). There is
-- deliberately nowhere to put selected text, page text, recognized text or image bytes.
CREATE TABLE message_context_items (
    id           TEXT    NOT NULL PRIMARY KEY,
    message_id   TEXT    NOT NULL REFERENCES messages (id) ON DELETE CASCADE,
    position     INTEGER NOT NULL CHECK (position >= 0),
    type         TEXT    NOT NULL,
    display_name TEXT    NOT NULL,
    file_path    TEXT,
    UNIQUE (message_id, position)
) STRICT;

-- Facts about the settings document that are worth keeping beside the history, such as which schema version it was
-- last written at. The settings themselves are not stored here.
CREATE TABLE settings_metadata (
    key        TEXT NOT NULL PRIMARY KEY,
    value      TEXT NOT NULL,
    updated_at TEXT NOT NULL
) STRICT;

-- People the user has told the Assistant about, and how each relates to the user ("Sara", "sister").
CREATE TABLE people (
    id           TEXT NOT NULL PRIMARY KEY,
    display_name TEXT NOT NULL CHECK (length(trim(display_name)) > 0),
    created_at   TEXT NOT NULL,
    updated_at   TEXT NOT NULL
) STRICT;

CREATE INDEX ix_people_display_name ON people (display_name COLLATE NOCASE);

CREATE TABLE person_relationships (
    person_id    TEXT NOT NULL REFERENCES people (id) ON DELETE CASCADE,
    relationship TEXT NOT NULL COLLATE NOCASE CHECK (length(trim(relationship)) > 0),
    PRIMARY KEY (person_id, relationship)
) STRICT, WITHOUT ROWID;

-- "Who is my sister?" looks people up by relationship.
CREATE INDEX ix_person_relationships_relationship ON person_relationships (relationship);

-- What the user has decided about a tool. A tool with no row is asked about every time. The tool's risk level is not
-- stored: it is fixed in code and a stored value must never be able to change it (PROJECT_SPEC 4.8).
CREATE TABLE tool_permissions (
    tool_name  TEXT NOT NULL PRIMARY KEY,
    decision   TEXT NOT NULL,
    decided_at TEXT NOT NULL
) STRICT;

-- What the Assistant did for the user: one row per tool run. A record says which tool ran, how risky it was, how it
-- ended and when. It holds no arguments, output or other content, and it outlives the conversation it came from (the
-- link is cleared when that conversation is deleted).
CREATE TABLE task_audit_records (
    id              TEXT NOT NULL PRIMARY KEY,
    conversation_id TEXT REFERENCES conversations (id) ON DELETE SET NULL,
    tool_name       TEXT NOT NULL,
    risk_level      TEXT NOT NULL,
    outcome         TEXT NOT NULL,
    started_at      TEXT NOT NULL,
    completed_at    TEXT
) STRICT;

CREATE INDEX ix_task_audit_records_started_at ON task_audit_records (started_at DESC);

-- Lets SQLite find a conversation's records quickly when clearing their link.
CREATE INDEX ix_task_audit_records_conversation_id ON task_audit_records (conversation_id)
    WHERE conversation_id IS NOT NULL;
