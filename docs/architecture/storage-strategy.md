# Storage Strategy

## SQLite Schema (High-Level)

### Media Metadata
- media
  - id (pk)
  - source_id
  - title
  - media_type (paged|video)
  - rating (nullable)
  - synopsis
  - language
  - tags
  - created_at
  - updated_at

- media_chapters
  - id (pk)
  - media_id (fk)
  - chapter_number
  - title
  - published_at

- media_streams
  - id (pk)
  - media_id (fk)
  - quality
  - codec
  - bitrate
  - url_hash

### Plugin Registry
- plugins
  - id (pk)
  - name
  - version
  - status (active|quarantined|disabled)
  - manifest_json
  - installed_at
  - updated_at

- plugin_failures
  - id (pk)
  - plugin_id (fk)
  - failure_type
  - occurred_at
  - details

### Media-Plugin Migration (user-controlled)
- media_migration_candidates
  - id (pk)
  - media_id (fk)
  - from_plugin_id (fk)
  - to_plugin_id (fk)
  - match_score
  - requested_by_user_id
  - requested_at
  - status (pending|approved|rejected)
  - decided_at
  - reason

- media_migration_map
  - id (pk)
  - media_id (fk)
  - from_plugin_id (fk)
  - to_plugin_id (fk)
  - from_external_id
  - to_external_id
  - migrated_at
  - migrated_by_user_id

### User Library
- library
  - id (pk)
  - media_id (fk)
  - user_id
  - added_at
  - source_id

### Watch/Read History
- history
  - id (pk)
  - media_id (fk)
  - plugin_id (fk)
  - external_id
  - user_id
  - position
  - completed
  - last_viewed_at

## Cache Invalidation Strategy

- Metadata cache uses ETag/Last-Modified when available.
- Soft TTL for library metadata; background refresh on access.
- Hard TTL for plugin results to avoid stale data.
- Invalidate on plugin version upgrade or manifest change.

## Migration Notes

- Migration is user-controlled; no automatic source switching.
- Migrations transfer tracked progress from a source plugin to another.
- Use match_score to track confidence when mapping across plugins.
- Migration decisions are audited and reversible.
- History is re-keyed by creating a new history row for the target plugin
  using the mapped external_id and preserved position/time data.
- Original history rows are retained for audit and rollback.

## Indexing Strategy

- Index media by source_id, media_type, title (fts5 if needed).
- Index history by user_id and last_viewed_at.
- Index plugins by status and name.
- Composite index for library (user_id, media_id).

## Notes

- Use WAL mode for concurrency.
- Migrations tracked in Aggregator.Storage.
- Large blobs (images, video segments) are never stored in SQLite.
