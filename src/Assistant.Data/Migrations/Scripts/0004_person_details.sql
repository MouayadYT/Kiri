-- What the Assistant knows of a person beyond their name and how they relate to the user (PROJECT_SPEC 3.5, 4.8, step 112): the
-- other names they are called, and the addresses a messaging provider can reach them by. Like the relationships in the first
-- migration, these belong to the person and are deleted with them. Names, aliases and addresses are private content: they are only
-- ever written and read as parameters, and never logged.

CREATE TABLE person_aliases (
    person_id TEXT NOT NULL REFERENCES people (id) ON DELETE CASCADE,
    alias     TEXT NOT NULL COLLATE NOCASE CHECK (length(trim(alias)) > 0),
    PRIMARY KEY (person_id, alias)
) STRICT, WITHOUT ROWID;

-- "Who is Bro?" looks people up by what they are called.
CREATE INDEX ix_person_aliases_alias ON person_aliases (alias);

-- A person is reached by a phone number, an email address or a username, and perhaps only on one service. An empty service means
-- any. A provider finds the person behind an address by its value.
CREATE TABLE person_identifiers (
    person_id TEXT NOT NULL REFERENCES people (id) ON DELETE CASCADE,
    kind      TEXT NOT NULL,
    service   TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
    value     TEXT NOT NULL COLLATE NOCASE CHECK (length(trim(value)) > 0),
    PRIMARY KEY (person_id, kind, service, value)
) STRICT, WITHOUT ROWID;

CREATE INDEX ix_person_identifiers_value ON person_identifiers (value);
