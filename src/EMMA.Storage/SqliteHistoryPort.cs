using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Storage;

/// <summary>
/// SQLite implementation for read/watch history entries.
/// </summary>
public sealed class SqliteHistoryPort(StorageOptions options) : IHistoryPort
{
    private readonly SqliteConnectionFactory _connectionFactory = new(options);

    public async Task UpsertAsync(MediaHistoryEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO history (
                id,
                media_id,
                plugin_id,
                external_id,
                user_id,
                position,
                completed,
                last_viewed_at
            ) VALUES (
                $id,
                $mediaId,
                $pluginId,
                $externalId,
                $userId,
                $position,
                $completed,
                $lastViewedAt
            )
            ON CONFLICT(id) DO UPDATE SET
                media_id = excluded.media_id,
                plugin_id = excluded.plugin_id,
                external_id = excluded.external_id,
                user_id = excluded.user_id,
                position = excluded.position,
                completed = excluded.completed,
                last_viewed_at = excluded.last_viewed_at;
            """;

        command.Parameters.AddWithValue("$id", entry.EntryId);
        command.Parameters.AddWithValue("$mediaId", entry.MediaId.Value);
        command.Parameters.AddWithValue("$pluginId", entry.PluginId);
        command.Parameters.AddWithValue("$externalId", entry.ExternalId);
        command.Parameters.AddWithValue("$userId", entry.UserId);
        command.Parameters.AddWithValue("$position", entry.Position);
        command.Parameters.AddWithValue("$completed", entry.Completed ? 1 : 0);
        command.Parameters.AddWithValue("$lastViewedAt", entry.LastViewedAtUtc.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MediaHistoryEntry>> GetHistoryAsync(
        string userId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, media_id, plugin_id, external_id, user_id, position, completed, last_viewed_at
            FROM history
            WHERE user_id = $userId
            ORDER BY last_viewed_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var results = new List<MediaHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new MediaHistoryEntry(
                reader.GetString(0),
                MediaId.Create(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetDouble(5),
                reader.GetInt32(6) == 1,
                DateTimeOffset.Parse(reader.GetString(7))));
        }

        return results;
    }

    public async Task DeleteForMediaAsync(
        MediaId mediaId,
        string pluginId,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM history
            WHERE media_id = $mediaId
              AND plugin_id = $pluginId
              AND user_id = $userId;
            """;
        command.Parameters.AddWithValue("$mediaId", mediaId.Value);
        command.Parameters.AddWithValue("$pluginId", pluginId ?? string.Empty);
        command.Parameters.AddWithValue("$userId", userId ?? string.Empty);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
