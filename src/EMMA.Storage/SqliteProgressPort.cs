using System.Globalization;
using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Storage;

public sealed class SqliteProgressPort(StorageOptions options) : IProgressPort
{
    private readonly SqliteConnectionFactory _connectionFactory = new(options);

    public async Task<PagedMediaProgress?> GetPagedProgressAsync(
        MediaId mediaId,
        string pluginId,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT external_id, position, completed, last_viewed_at
            FROM history
            WHERE media_id = $mediaId
              AND plugin_id = $pluginId
              AND user_id = $userId
              AND id LIKE 'paged::%'
            ORDER BY datetime(last_viewed_at) DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$mediaId", mediaId.Value);
        command.Parameters.AddWithValue("$pluginId", pluginId ?? string.Empty);
        command.Parameters.AddWithValue("$userId", userId ?? string.Empty);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var chapterId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var rawPosition = reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1);
        var completed = !reader.IsDBNull(2) && reader.GetInt32(2) != 0;
        var lastViewedAt = reader.IsDBNull(3)
            ? DateTimeOffset.UtcNow
            : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture);

        return new PagedMediaProgress(
            mediaId,
            pluginId ?? string.Empty,
            chapterId,
            Math.Max(0, (int)Math.Round(rawPosition)),
            completed,
            lastViewedAt);
    }

    public async Task SetPagedProgressAsync(
        MediaId mediaId,
        string pluginId,
        string chapterId,
        int pageIndex,
        bool completed,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var safePluginId = pluginId ?? string.Empty;
        var safeUserId = userId ?? string.Empty;
        var safeChapterId = chapterId ?? string.Empty;
        var id = $"paged::{safeUserId}::{safePluginId}::{mediaId.Value}::{safeChapterId}";

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
                position = excluded.position,
                completed = excluded.completed,
                last_viewed_at = excluded.last_viewed_at;
            """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$mediaId", mediaId.Value);
        command.Parameters.AddWithValue("$pluginId", safePluginId);
        command.Parameters.AddWithValue("$externalId", safeChapterId);
        command.Parameters.AddWithValue("$userId", safeUserId);
        command.Parameters.AddWithValue("$position", Math.Max(0, pageIndex));
        command.Parameters.AddWithValue("$completed", completed ? 1 : 0);
        command.Parameters.AddWithValue("$lastViewedAt", now.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<VideoMediaProgress?> GetVideoProgressAsync(
        MediaId mediaId,
        string pluginId,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT position, completed, last_viewed_at
            FROM history
            WHERE media_id = $mediaId
              AND plugin_id = $pluginId
              AND user_id = $userId
              AND id LIKE 'video::%'
            ORDER BY datetime(last_viewed_at) DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$mediaId", mediaId.Value);
        command.Parameters.AddWithValue("$pluginId", pluginId ?? string.Empty);
        command.Parameters.AddWithValue("$userId", userId ?? string.Empty);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var position = reader.IsDBNull(0) ? 0.0 : reader.GetDouble(0);
        var completed = !reader.IsDBNull(1) && reader.GetInt32(1) != 0;
        var lastViewedAt = reader.IsDBNull(2)
            ? DateTimeOffset.UtcNow
            : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture);

        return new VideoMediaProgress(
            mediaId,
            pluginId ?? string.Empty,
            Math.Max(0, position),
            completed,
            lastViewedAt);
    }

    public async Task SetVideoProgressAsync(
        MediaId mediaId,
        string pluginId,
        double positionSeconds,
        bool completed,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var safePluginId = pluginId ?? string.Empty;
        var safeUserId = userId ?? string.Empty;
        var id = $"video::{safeUserId}::{safePluginId}::{mediaId.Value}";

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
                position = excluded.position,
                completed = excluded.completed,
                last_viewed_at = excluded.last_viewed_at;
            """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$mediaId", mediaId.Value);
        command.Parameters.AddWithValue("$pluginId", safePluginId);
        command.Parameters.AddWithValue("$externalId", mediaId.Value);
        command.Parameters.AddWithValue("$userId", safeUserId);
        command.Parameters.AddWithValue("$position", Math.Max(0, positionSeconds));
        command.Parameters.AddWithValue("$completed", completed ? 1 : 0);
        command.Parameters.AddWithValue("$lastViewedAt", now.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
