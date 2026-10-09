-- Saving conversations a message at a time (PROJECT_SPEC 3.5, 4.3): what a saved assistant message needs besides its text.
--
-- The conventions of the first migration hold: a Guid is lowercase text, a moment is UTC ISO-8601 text, an enum is stored
-- by name without a CHECK, tables are STRICT and have no BLOB column.

-- How an assistant message ended: Complete, Stopped (the user stopped the answer) or Failed. A stopped answer is drawn
-- with a note that says so, and an answer stopped before its first words is that note alone, so the outcome is kept
-- with the message. Every message that was saved before this column is whole.
ALTER TABLE messages ADD COLUMN outcome TEXT NOT NULL DEFAULT 'Complete';

-- The structured parts of an assistant message that are not prose: a calculation's result card, a block of code, a
-- gallery of images, a list of files. A card is described as data that its presenter draws again (kind + JSON), never
-- with content: an image or a file is a path, not its bytes or its text (PROJECT_SPEC 3.5). Which kinds exist is the
-- presenter's business, so a new kind needs no migration.
--
-- text_offset says where the card sits among the message's prose: how many characters of messages.text come before it.
-- It is what keeps a card between the paragraphs it was shown between.
CREATE TABLE message_cards (
    message_id  TEXT    NOT NULL REFERENCES messages (id) ON DELETE CASCADE,
    position    INTEGER NOT NULL CHECK (position >= 0),
    kind        TEXT    NOT NULL CHECK (length(kind) BETWEEN 1 AND 64),
    text_offset INTEGER NOT NULL CHECK (text_offset >= 0),
    data        TEXT    NOT NULL CHECK (json_valid(data)),
    PRIMARY KEY (message_id, position)
) STRICT, WITHOUT ROWID;
