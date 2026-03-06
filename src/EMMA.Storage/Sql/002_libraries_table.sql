PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS libraries (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    created_at TEXT NOT NULL
);

INSERT OR IGNORE INTO libraries (id, name, created_at)
VALUES ('library::Library', 'Library', datetime('now'));

INSERT OR IGNORE INTO libraries (id, name, created_at)
SELECT DISTINCT user_id, user_id, datetime('now')
FROM library
WHERE user_id IS NOT NULL
  AND trim(user_id) <> '';

CREATE INDEX IF NOT EXISTS idx_libraries_name ON libraries(name);
