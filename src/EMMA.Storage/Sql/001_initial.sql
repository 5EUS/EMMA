PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS media (
    id TEXT PRIMARY KEY,
    source_id TEXT NOT NULL,
    title TEXT NOT NULL,
    media_type TEXT NOT NULL,
    rating TEXT,
    synopsis TEXT,
    language TEXT,
    tags TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS media_chapters (
    id TEXT NOT NULL,
    media_id TEXT NOT NULL,
    chapter_number INTEGER NOT NULL,
    title TEXT NOT NULL,
    published_at TEXT,
    PRIMARY KEY (id, media_id),
    FOREIGN KEY (media_id) REFERENCES media(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS media_streams (
    id TEXT NOT NULL,
    media_id TEXT NOT NULL,
    quality TEXT NOT NULL,
    codec TEXT NOT NULL,
    bitrate INTEGER NOT NULL,
    url_hash TEXT NOT NULL,
    PRIMARY KEY (id, media_id),
    FOREIGN KEY (media_id) REFERENCES media(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS plugins (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    version TEXT NOT NULL,
    status TEXT NOT NULL,
    manifest_json TEXT NOT NULL,
    installed_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS plugin_failures (
    id TEXT PRIMARY KEY,
    plugin_id TEXT NOT NULL,
    failure_type TEXT NOT NULL,
    occurred_at TEXT NOT NULL,
    details TEXT,
    FOREIGN KEY (plugin_id) REFERENCES plugins(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS media_migration_candidates (
    id TEXT PRIMARY KEY,
    media_id TEXT NOT NULL,
    from_plugin_id TEXT NOT NULL,
    to_plugin_id TEXT NOT NULL,
    match_score REAL NOT NULL,
    requested_by_user_id TEXT NOT NULL,
    requested_at TEXT NOT NULL,
    status TEXT NOT NULL,
    decided_at TEXT,
    reason TEXT,
    FOREIGN KEY (media_id) REFERENCES media(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS media_migration_map (
    id TEXT PRIMARY KEY,
    media_id TEXT NOT NULL,
    from_plugin_id TEXT NOT NULL,
    to_plugin_id TEXT NOT NULL,
    from_external_id TEXT NOT NULL,
    to_external_id TEXT NOT NULL,
    migrated_at TEXT NOT NULL,
    migrated_by_user_id TEXT NOT NULL,
    FOREIGN KEY (media_id) REFERENCES media(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS library (
    id TEXT PRIMARY KEY,
    media_id TEXT NOT NULL,
    user_id TEXT NOT NULL,
    added_at TEXT NOT NULL,
    source_id TEXT NOT NULL,
    FOREIGN KEY (media_id) REFERENCES media(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS history (
    id TEXT PRIMARY KEY,
    media_id TEXT NOT NULL,
    plugin_id TEXT NOT NULL,
    external_id TEXT NOT NULL,
    user_id TEXT NOT NULL,
    position REAL NOT NULL,
    completed INTEGER NOT NULL,
    last_viewed_at TEXT NOT NULL,
    FOREIGN KEY (media_id) REFERENCES media(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_media_title ON media(title);
CREATE INDEX IF NOT EXISTS idx_media_source ON media(source_id);
CREATE INDEX IF NOT EXISTS idx_media_type ON media(media_type);
CREATE INDEX IF NOT EXISTS idx_history_user ON history(user_id, last_viewed_at);
CREATE INDEX IF NOT EXISTS idx_plugins_status ON plugins(status);
CREATE INDEX IF NOT EXISTS idx_library_user_media ON library(user_id, media_id);
