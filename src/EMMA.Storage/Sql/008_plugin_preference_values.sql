CREATE TABLE IF NOT EXISTS plugin_preference_values (
    plugin_id TEXT NOT NULL,
    field_key TEXT NOT NULL,
    field_type TEXT NOT NULL,
    protection_level TEXT NOT NULL,
    is_secret INTEGER NOT NULL,
    json_value TEXT,
    secret_payload TEXT,
    value_state TEXT NOT NULL,
    schema_version INTEGER NOT NULL,
    validation_error TEXT,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (plugin_id, field_key)
);

CREATE INDEX IF NOT EXISTS idx_plugin_preference_values_plugin_id
    ON plugin_preference_values(plugin_id);