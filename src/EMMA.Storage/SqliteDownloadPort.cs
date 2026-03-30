using System.Globalization;
using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Storage;

public sealed class SqliteDownloadPort(StorageOptions options) : IDownloadPort
{
    private readonly SqliteConnectionFactory _connectionFactory = new(options);

    public async Task<DownloadJobRecord> CreateJobAsync(DownloadEnqueueRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new DownloadJobRecord(
            Guid.NewGuid().ToString("n"),
            request.PluginId.Trim(),
            request.MediaId.Trim(),
            request.MediaType.Trim().ToLowerInvariant(),
            NormalizeNullable(request.ChapterId),
            NormalizeNullable(request.StreamId),
            DownloadJobState.Queued,
            0,
            0,
            0,
            null,
            now,
            now,
            null,
            null);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO download_jobs (
                id,
                plugin_id,
                media_id,
                media_type,
                chapter_id,
                stream_id,
                state,
                progress_completed,
                progress_total,
                bytes_downloaded,
                error_message,
                created_at,
                updated_at,
                started_at,
                completed_at
            ) VALUES (
                $id,
                $pluginId,
                $mediaId,
                $mediaType,
                $chapterId,
                $streamId,
                $state,
                $progressCompleted,
                $progressTotal,
                $bytesDownloaded,
                $errorMessage,
                $createdAt,
                $updatedAt,
                $startedAt,
                $completedAt
            );
            """;

        BindRecord(command, record);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return record;
    }

    public async Task<IReadOnlyList<DownloadJobRecord>> ListJobsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                plugin_id,
                media_id,
                media_type,
                chapter_id,
                stream_id,
                state,
                progress_completed,
                progress_total,
                bytes_downloaded,
                error_message,
                created_at,
                updated_at,
                started_at,
                completed_at
            FROM download_jobs
            ORDER BY datetime(updated_at) DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        return await ReadRecordsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<DownloadJobRecord>> ListJobsByStateAsync(
        IReadOnlyList<DownloadJobState> states,
        int limit,
        CancellationToken cancellationToken)
    {
        if (states.Count == 0)
        {
            return [];
        }

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        var placeholders = new List<string>(states.Count);
        for (var i = 0; i < states.Count; i++)
        {
            var key = $"$state{i}";
            placeholders.Add(key);
            command.Parameters.AddWithValue(key, states[i].ToString());
        }

        command.CommandText = $"""
            SELECT
                id,
                plugin_id,
                media_id,
                media_type,
                chapter_id,
                stream_id,
                state,
                progress_completed,
                progress_total,
                bytes_downloaded,
                error_message,
                created_at,
                updated_at,
                started_at,
                completed_at
            FROM download_jobs
            WHERE state IN ({string.Join(",", placeholders)})
            ORDER BY datetime(updated_at) ASC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        return await ReadRecordsAsync(command, cancellationToken);
    }

    public async Task<DownloadJobRecord?> GetJobAsync(string jobId, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                plugin_id,
                media_id,
                media_type,
                chapter_id,
                stream_id,
                state,
                progress_completed,
                progress_total,
                bytes_downloaded,
                error_message,
                created_at,
                updated_at,
                started_at,
                completed_at
            FROM download_jobs
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapRecord(reader);
    }

    public async Task<bool> UpdateStateAsync(
        string jobId,
        DownloadJobState state,
        string? errorMessage,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? completedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE download_jobs
            SET
                state = $state,
                error_message = $errorMessage,
                updated_at = $updatedAt,
                started_at = $startedAt,
                completed_at = $completedAt
            WHERE id = $id;
            """;

        command.Parameters.AddWithValue("$id", jobId);
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$errorMessage", (object?)NormalizeNullable(errorMessage) ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", updatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$startedAt", startedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$completedAt", completedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<bool> UpdateProgressAsync(
        string jobId,
        int completed,
        int total,
        long bytesDownloaded,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE download_jobs
            SET
                progress_completed = $completed,
                progress_total = $total,
                bytes_downloaded = $bytesDownloaded,
                updated_at = $updatedAt
            WHERE id = $id;
            """;

        command.Parameters.AddWithValue("$id", jobId);
        command.Parameters.AddWithValue("$completed", Math.Max(0, completed));
        command.Parameters.AddWithValue("$total", Math.Max(0, total));
        command.Parameters.AddWithValue("$bytesDownloaded", Math.Max(0, bytesDownloaded));
        command.Parameters.AddWithValue("$updatedAt", updatedAtUtc.ToString("O", CultureInfo.InvariantCulture));

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<bool> DeleteJobAsync(string jobId, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM download_jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", jobId);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    private static async Task<IReadOnlyList<DownloadJobRecord>> ReadRecordsAsync(
        Microsoft.Data.Sqlite.SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var records = new List<DownloadJobRecord>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(MapRecord(reader));
        }

        return records;
    }

    private static DownloadJobRecord MapRecord(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        return new DownloadJobRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ReadNullableString(reader, 4),
            ReadNullableString(reader, 5),
            ParseState(reader.GetString(6)),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt64(9),
            ReadNullableString(reader, 10),
            ParseDate(reader.GetString(11)),
            ParseDate(reader.GetString(12)),
            ReadNullableDate(reader, 13),
            ReadNullableDate(reader, 14));
    }

    private static void BindRecord(Microsoft.Data.Sqlite.SqliteCommand command, DownloadJobRecord record)
    {
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$pluginId", record.PluginId);
        command.Parameters.AddWithValue("$mediaId", record.MediaId);
        command.Parameters.AddWithValue("$mediaType", record.MediaType);
        command.Parameters.AddWithValue("$chapterId", (object?)record.ChapterId ?? DBNull.Value);
        command.Parameters.AddWithValue("$streamId", (object?)record.StreamId ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", record.State.ToString());
        command.Parameters.AddWithValue("$progressCompleted", Math.Max(0, record.ProgressCompleted));
        command.Parameters.AddWithValue("$progressTotal", Math.Max(0, record.ProgressTotal));
        command.Parameters.AddWithValue("$bytesDownloaded", Math.Max(0, record.BytesDownloaded));
        command.Parameters.AddWithValue("$errorMessage", (object?)record.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", record.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedAt", record.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$startedAt", record.StartedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$completedAt", record.CompletedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
    }

    private static DownloadJobState ParseState(string value)
    {
        if (Enum.TryParse<DownloadJobState>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return DownloadJobState.Queued;
    }

    private static DateTimeOffset ParseDate(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ReadNullableDate(Microsoft.Data.Sqlite.SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return ParseDate(reader.GetString(ordinal));
    }

    private static string? ReadNullableString(Microsoft.Data.Sqlite.SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return NormalizeNullable(reader.GetString(ordinal));
    }

    private static string? NormalizeNullable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
