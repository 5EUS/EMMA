CREATE TABLE nodes (
    plugin_id       TEXT    NOT NULL,
    local_id        TEXT    NOT NULL,
    kind            TEXT    NOT NULL,
    title           TEXT    NOT NULL,
    parent_local_id TEXT,
    thumbnail       TEXT,
    props           TEXT    NOT NULL,
    updated_at      INTEGER NOT NULL,
    -- 1 when the row came from the plugin's `get` (full details), 0 when it came from a listing or search, which may
    -- leave out expensive props. A later listing that changes the node resets it; one that does not change it keeps it.
    detailed        INTEGER NOT NULL DEFAULT 0 CHECK (detailed IN (0, 1)),
    PRIMARY KEY (plugin_id, local_id)
) STRICT, WITHOUT ROWID;

CREATE INDEX idx_nodes_parent ON nodes (plugin_id, parent_local_id);

-- A node's children as the host last listed them, in the plugin's order. The state row exists only once a listing is
-- complete, so a half-written listing is never served. Child rows live in `nodes`; replacing a listing never deletes
-- them (progress and library entries still refer to nodes a plugin has stopped listing).
CREATE TABLE node_listing_state (
    plugin_id       TEXT    NOT NULL,
    parent_local_id TEXT    NOT NULL,
    total           INTEGER NOT NULL CHECK (total >= 0),
    listed_at       INTEGER NOT NULL,
    PRIMARY KEY (plugin_id, parent_local_id)
) STRICT, WITHOUT ROWID;

CREATE TABLE node_listings (
    plugin_id       TEXT    NOT NULL,
    parent_local_id TEXT    NOT NULL,
    position        INTEGER NOT NULL CHECK (position >= 0),
    local_id        TEXT    NOT NULL,
    PRIMARY KEY (plugin_id, parent_local_id, position),
    FOREIGN KEY (plugin_id, parent_local_id) REFERENCES node_listing_state (plugin_id, parent_local_id) ON DELETE CASCADE
) STRICT, WITHOUT ROWID;

-- What the disk asset cache holds, so it survives a restart. `file` is a name inside the cache directory (never a
-- path, so the directory can move). The cache is purgeable: deleting rows and files loses nothing but speed.
CREATE TABLE asset_cache (
    cache_key    TEXT    NOT NULL PRIMARY KEY,
    plugin_id    TEXT    NOT NULL,
    local_id     TEXT    NOT NULL,
    file         TEXT    NOT NULL,
    mime         TEXT    NOT NULL,
    bytes        INTEGER NOT NULL CHECK (bytes >= 0),
    fetched_at   INTEGER NOT NULL,
    last_used_at INTEGER NOT NULL
) STRICT, WITHOUT ROWID;

CREATE INDEX idx_asset_cache_node ON asset_cache (plugin_id, local_id);

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

-- A user's libraries. The default library is not stored until it is first needed (the store creates it), so a
-- user who never saved anything has no rows. Names compare without regard to case; the row keeps the casing the
-- library was created with.
CREATE TABLE libraries (
    user_id    TEXT    NOT NULL,
    name       TEXT    NOT NULL COLLATE NOCASE,
    created_at INTEGER NOT NULL,
    PRIMARY KEY (user_id, name)
) STRICT, WITHOUT ROWID;

-- A saved node. A node is in at most one library per user (moving it changes library_name), and deleting a library
-- deletes its entries. Progress and downloads are never touched by either.
CREATE TABLE library (
    user_id      TEXT    NOT NULL,
    plugin_id    TEXT    NOT NULL,
    local_id     TEXT    NOT NULL,
    library_name TEXT    NOT NULL COLLATE NOCASE,
    added_at     INTEGER NOT NULL,
    PRIMARY KEY (user_id, plugin_id, local_id),
    FOREIGN KEY (user_id, library_name) REFERENCES libraries (user_id, name) ON DELETE CASCADE
) STRICT, WITHOUT ROWID;

CREATE INDEX idx_library_added ON library (user_id, library_name, added_at DESC);

-- Small per-user choices the app wants remembered: the history retention, a series' reverse-order choice, the
-- parallel-downloads limit. `scope` says what the choice is about (a series, the history, the app) and the host
-- reserves scopes that start with "emma." for itself. Values are text; a client that needs structure encodes it.
CREATE TABLE app_settings (
    user_id     TEXT    NOT NULL,
    scope       TEXT    NOT NULL,
    setting_key TEXT    NOT NULL,
    value       TEXT    NOT NULL,
    updated_at  INTEGER NOT NULL,
    PRIMARY KEY (user_id, scope, setting_key)
) STRICT, WITHOUT ROWID;

CREATE TABLE download_jobs (
    id               TEXT    NOT NULL PRIMARY KEY,
    plugin_id        TEXT    NOT NULL,
    local_id         TEXT    NOT NULL,
    role             TEXT    NOT NULL,
    hint             TEXT,
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
CREATE INDEX idx_download_jobs_node ON download_jobs (plugin_id, local_id, role);

-- The leaves a job downloads, in the order the node tree lists them. An item with a file is stored: `file` names
-- a file in the downloads directory (a hash of the leaf, so two jobs that cover the same leaf share one file and the
-- file goes when no item names it). Deleting a job deletes its items.
CREATE TABLE download_items (
    job_id        TEXT    NOT NULL REFERENCES download_jobs (id) ON DELETE CASCADE,
    position      INTEGER NOT NULL CHECK (position >= 0),
    plugin_id     TEXT    NOT NULL,
    local_id      TEXT    NOT NULL,
    role          TEXT    NOT NULL,
    file          TEXT,
    mime          TEXT,
    bytes         INTEGER NOT NULL CHECK (bytes >= 0),
    error_message TEXT,
    PRIMARY KEY (job_id, position)
) STRICT, WITHOUT ROWID;

CREATE INDEX idx_download_items_leaf ON download_items (plugin_id, local_id, role) WHERE file IS NOT NULL;
CREATE INDEX idx_download_items_file ON download_items (file) WHERE file IS NOT NULL;

CREATE TABLE plugins (
    id            TEXT    NOT NULL PRIMARY KEY,
    version       TEXT    NOT NULL,
    status        INTEGER NOT NULL CHECK (status BETWEEN 0 AND 2),
    manifest_json TEXT    NOT NULL,
    signer        TEXT,
    installed_at  INTEGER NOT NULL,
    updated_at    INTEGER NOT NULL
) STRICT, WITHOUT ROWID;

-- The user's values for what a plugin declared in meta.describe. One row per value: a multi-choice setting has
-- several, ordered by position. A secret has exactly one, stored as the text ISecretBox made (never in clear), so
-- `value` is not readable for secret = 1. Settings go when the plugin is uninstalled, secrets with them, but survive
-- an upgrade (the registry updates the plugins row in place rather than replacing it).
CREATE TABLE plugin_settings (
    plugin_id   TEXT    NOT NULL REFERENCES plugins (id) ON DELETE CASCADE,
    setting_key TEXT    NOT NULL,
    position    INTEGER NOT NULL CHECK (position >= 0),
    value       TEXT    NOT NULL,
    secret      INTEGER NOT NULL CHECK (secret IN (0, 1)),
    updated_at  INTEGER NOT NULL,
    PRIMARY KEY (plugin_id, setting_key, position)
) STRICT, WITHOUT ROWID;

CREATE TABLE trusted_delegation_versions (
    repository_id TEXT    NOT NULL PRIMARY KEY,
    version       INTEGER NOT NULL CHECK (version >= 0),
    updated_at    INTEGER NOT NULL
) STRICT, WITHOUT ROWID;
