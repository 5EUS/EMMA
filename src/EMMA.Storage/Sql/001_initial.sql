CREATE TABLE nodes (
    plugin_id       TEXT    NOT NULL,
    local_id        TEXT    NOT NULL,
    kind            TEXT    NOT NULL,
    title           TEXT    NOT NULL,
    parent_local_id TEXT,
    thumbnail       TEXT,
    props           TEXT    NOT NULL,
    updated_at      INTEGER NOT NULL,
    PRIMARY KEY (plugin_id, local_id)
) STRICT, WITHOUT ROWID;

CREATE INDEX idx_nodes_parent ON nodes (plugin_id, parent_local_id);

CREATE TABLE progress (
    user_id    TEXT    NOT NULL,
    plugin_id  TEXT    NOT NULL,
    local_id   TEXT    NOT NULL,
    unit       TEXT    NOT NULL,
    value      REAL    NOT NULL CHECK (value >= 0),
    total      REAL    CHECK (total IS NULL OR total >= value),
    completed  INTEGER NOT NULL CHECK (completed IN (0, 1)),
    updated_at INTEGER NOT NULL,
    PRIMARY KEY (user_id, plugin_id, local_id)
) STRICT, WITHOUT ROWID;

CREATE INDEX idx_progress_recent ON progress (user_id, updated_at DESC);

CREATE TABLE library (
    user_id   TEXT    NOT NULL,
    plugin_id TEXT    NOT NULL,
    local_id  TEXT    NOT NULL,
    added_at  INTEGER NOT NULL,
    PRIMARY KEY (user_id, plugin_id, local_id)
) STRICT, WITHOUT ROWID;

CREATE INDEX idx_library_added ON library (user_id, added_at DESC);

CREATE TABLE download_jobs (
    id               TEXT    NOT NULL PRIMARY KEY,
    plugin_id        TEXT    NOT NULL,
    local_id         TEXT    NOT NULL,
    role             TEXT    NOT NULL,
    state            INTEGER NOT NULL CHECK (state BETWEEN 0 AND 5),
    items_completed  INTEGER NOT NULL CHECK (items_completed >= 0),
    items_total      INTEGER NOT NULL CHECK (items_total >= 0),
    bytes_downloaded INTEGER NOT NULL CHECK (bytes_downloaded >= 0),
    error_message    TEXT,
    created_at       INTEGER NOT NULL,
    updated_at       INTEGER NOT NULL,
    started_at       INTEGER,
    completed_at     INTEGER
) STRICT;

CREATE INDEX idx_download_jobs_state ON download_jobs (state, updated_at DESC);

CREATE TABLE plugins (
    id            TEXT    NOT NULL PRIMARY KEY,
    version       TEXT    NOT NULL,
    status        INTEGER NOT NULL CHECK (status BETWEEN 0 AND 2),
    manifest_json TEXT    NOT NULL,
    signer        TEXT,
    installed_at  INTEGER NOT NULL,
    updated_at    INTEGER NOT NULL
) STRICT, WITHOUT ROWID;

CREATE TABLE trusted_delegation_versions (
    repository_id TEXT    NOT NULL PRIMARY KEY,
    version       INTEGER NOT NULL CHECK (version >= 0),
    updated_at    INTEGER NOT NULL
) STRICT, WITHOUT ROWID;
