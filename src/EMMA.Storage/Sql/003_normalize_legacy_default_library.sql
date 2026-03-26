PRAGMA foreign_keys = ON;

INSERT OR IGNORE INTO libraries (id, name, created_at)
VALUES ('library::Library', 'Library', datetime('now'));

INSERT OR IGNORE INTO library (id, media_id, user_id, added_at, source_id)
SELECT 'library::Library:' || media_id,
       media_id,
       'library::Library',
       added_at,
       source_id
FROM library
WHERE lower(user_id) = 'default'
   OR lower(user_id) = 'library::default';

DELETE FROM library
WHERE lower(user_id) = 'default'
   OR lower(user_id) = 'library::default';

DELETE FROM libraries
WHERE lower(id) = 'default'
   OR lower(id) = 'library::default';
