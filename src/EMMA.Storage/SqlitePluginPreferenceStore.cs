using EMMA.Application.Ports;

namespace EMMA.Storage;

public sealed class SqlitePluginPreferenceStore(StorageOptions options) : IPluginPreferenceStore
{
    private readonly SqliteConnectionFactory _connectionFactory = new(options);

    public async Task<IReadOnlyList<PluginPreferenceStoreRecord>> ListByPluginAsync(
        string pluginId,
        CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT plugin_id, field_key, field_type, protection_level, is_secret, json_value, secret_payload, value_state, schema_version, validation_error, updated_at_utc
            FROM plugin_preference_values
            WHERE plugin_id = $pluginId
            ORDER BY field_key;
            """;
        command.Parameters.AddWithValue("$pluginId", pluginId);

        var results = new List<PluginPreferenceStoreRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadRecord(reader));
        }

        return results;
    }

    public async Task<PluginPreferenceStoreRecord?> GetAsync(
        string pluginId,
        string fieldKey,
        CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT plugin_id, field_key, field_type, protection_level, is_secret, json_value, secret_payload, value_state, schema_version, validation_error, updated_at_utc
            FROM plugin_preference_values
            WHERE plugin_id = $pluginId AND field_key = $fieldKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$pluginId", pluginId);
        command.Parameters.AddWithValue("$fieldKey", fieldKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadRecord(reader);
    }

    public async Task UpsertAsync(PluginPreferenceStoreRecord record, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO plugin_preference_values (
                plugin_id,
                field_key,
                field_type,
                protection_level,
                is_secret,
                json_value,
                secret_payload,
                value_state,
                schema_version,
                validation_error,
                updated_at_utc
            ) VALUES (
                $pluginId,
                $fieldKey,
                $fieldType,
                $protectionLevel,
                $isSecret,
                $jsonValue,
                $secretPayload,
                $valueState,
                $schemaVersion,
                $validationError,
                $updatedAtUtc
            )
            ON CONFLICT(plugin_id, field_key) DO UPDATE SET
                field_type = excluded.field_type,
                protection_level = excluded.protection_level,
                is_secret = excluded.is_secret,
                json_value = excluded.json_value,
                secret_payload = excluded.secret_payload,
                value_state = excluded.value_state,
                schema_version = excluded.schema_version,
                validation_error = excluded.validation_error,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$pluginId", record.PluginId);
        command.Parameters.AddWithValue("$fieldKey", record.FieldKey);
        command.Parameters.AddWithValue("$fieldType", record.FieldType);
        command.Parameters.AddWithValue("$protectionLevel", record.ProtectionLevel);
        command.Parameters.AddWithValue("$isSecret", record.IsSecret ? 1 : 0);
        command.Parameters.AddWithValue("$jsonValue", (object?)record.JsonValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$secretPayload", (object?)record.SecretPayload ?? DBNull.Value);
        command.Parameters.AddWithValue("$valueState", record.ValueState);
        command.Parameters.AddWithValue("$schemaVersion", record.SchemaVersion);
        command.Parameters.AddWithValue("$validationError", (object?)record.ValidationError ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAtUtc", record.UpdatedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string pluginId, string fieldKey, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM plugin_preference_values WHERE plugin_id = $pluginId AND field_key = $fieldKey;";
        command.Parameters.AddWithValue("$pluginId", pluginId);
        command.Parameters.AddWithValue("$fieldKey", fieldKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PluginPreferenceStoreRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        return new PluginPreferenceStoreRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4) != 0,
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetString(10));
    }
}