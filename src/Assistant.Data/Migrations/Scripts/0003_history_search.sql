-- Searching history on the device (PROJECT_SPEC 3.5, 4.3): a full-text index over the messages and over the titles of
-- conversations, kept up to date by triggers so that every way a row can change, a cascade included, changes the index
-- with it. The index is derived data and holds no more than the text it was made from, which is deleted with it.

-- Messages get an explicit integer key. An index over another table names its rows by rowid, and a table that has no
-- INTEGER PRIMARY KEY may be renumbered by VACUUM, which would leave the index pointing at the wrong messages (a backup
-- is made with VACUUM INTO). The table is rebuilt the way SQLite documents it: create, copy, drop, rename. The migrator
-- turns foreign keys off around it, so dropping the old table does not delete the rows that refer to it.
CREATE TABLE messages_rebuilt (
    seq             INTEGER PRIMARY KEY,
    id              TEXT    NOT NULL UNIQUE,
    conversation_id TEXT    NOT NULL REFERENCES conversations (id) ON DELETE CASCADE,
    position        INTEGER NOT NULL CHECK (position >= 0),
    role            TEXT    NOT NULL,
    text            TEXT    NOT NULL,
    created_at      TEXT    NOT NULL,
    outcome         TEXT    NOT NULL DEFAULT 'Complete',
    UNIQUE (conversation_id, position)
) STRICT;

INSERT INTO messages_rebuilt (id, conversation_id, position, role, text, created_at, outcome)
    SELECT id, conversation_id, position, role, text, created_at, outcome FROM messages ORDER BY rowid;

DROP TABLE messages;
ALTER TABLE messages_rebuilt RENAME TO messages;

-- The words of every message. The index holds no copy of the text: it reads it from messages when it needs it (external
-- content), so the text is stored once. A word is found by how it begins, without regard to case or accents.
CREATE VIRTUAL TABLE message_search USING fts5(
    text,
    content = 'messages',
    content_rowid = 'seq',
    tokenize = 'unicode61 remove_diacritics 2'
);

-- An external-content index is told about each change; it cannot see them. A change to a message's text takes out the
-- old words and puts in the new ones, and the text a delete names must be the text that was indexed.
CREATE TRIGGER messages_search_after_insert AFTER INSERT ON messages BEGIN
    INSERT INTO message_search (rowid, text) VALUES (new.seq, new.text);
END;

CREATE TRIGGER messages_search_after_delete AFTER DELETE ON messages BEGIN
    INSERT INTO message_search (message_search, rowid, text) VALUES ('delete', old.seq, old.text);
END;

CREATE TRIGGER messages_search_after_update AFTER UPDATE OF text ON messages BEGIN
    INSERT INTO message_search (message_search, rowid, text) VALUES ('delete', old.seq, old.text);
    INSERT INTO message_search (rowid, text) VALUES (new.seq, new.text);
END;

-- Index the messages that are already saved.
INSERT INTO message_search (message_search) VALUES ('rebuild');

-- The words of every conversation's title. Titles are short, so this index keeps its own copy; it is found by the
-- conversation it belongs to.
CREATE VIRTUAL TABLE conversation_title_search USING fts5(
    title,
    conversation_id UNINDEXED,
    tokenize = 'unicode61 remove_diacritics 2'
);

CREATE TRIGGER conversations_title_search_after_insert AFTER INSERT ON conversations BEGIN
    INSERT INTO conversation_title_search (title, conversation_id) VALUES (new.title, new.id);
END;

CREATE TRIGGER conversations_title_search_after_update AFTER UPDATE OF title ON conversations BEGIN
    DELETE FROM conversation_title_search WHERE conversation_id = old.id;
    INSERT INTO conversation_title_search (title, conversation_id) VALUES (new.title, new.id);
END;

CREATE TRIGGER conversations_title_search_after_delete AFTER DELETE ON conversations BEGIN
    DELETE FROM conversation_title_search WHERE conversation_id = old.id;
END;

INSERT INTO conversation_title_search (title, conversation_id) SELECT title, id FROM conversations;
