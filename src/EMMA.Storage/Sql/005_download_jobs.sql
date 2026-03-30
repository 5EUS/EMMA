CREATE TABLE IF NOT EXISTS download_jobs (
    id TEXT PRIMARY KEY,
    plugin_id TEXT NOT NULL,
    media_id TEXT NOT NULL,
    media_type TEXT NOT NULL,
    chapter_id TEXT,
    stream_id TEXT,
    state TEXT NOT NULL,
    progress_completed INTEGER NOT NULL,
    progress_total INTEGER NOT NULL,
    bytes_downloaded INTEGER NOT NULL,
    error_message TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    started_at TEXT,
    completed_at TEXT
);

CREATE INDEX IF NOT EXISTS idx_download_jobs_state_updated
    ON download_jobs(state, updated_at);

CREATE INDEX IF NOT EXISTS idx_download_jobs_media
    ON download_jobs(plugin_id, media_id);
