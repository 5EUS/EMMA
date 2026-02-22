using System.Text.Json;
using EMMA.Application.Ports;
using EMMA.Domain;
using Microsoft.Data.Sqlite;

namespace EMMA.Storage;

/// <summary>
/// SQLite implementation for media metadata and chapters.
/// </summary>
public sealed class SqliteMediaCatalogPort(StorageOptions options) : IMediaCatalogPort
{
    private readonly SqliteConnectionFactory _connectionFactory = new(options);

    public async Task UpsertMediaAsync(MediaMetadata media, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var tagsJson = JsonSerializer.Serialize(media.Tags ?? Array.Empty<string>());

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO media (
                id,
                source_id,
                title,
                media_type,
                rating,
                synopsis,
                language,
                tags,
                created_at,
                updated_at
            ) VALUES (
                $id,
                $sourceId,
                $title,
                $mediaType,
                $rating,
                $synopsis,
                $language,
                $tags,
                $createdAt,
                $updatedAt
            )
            ON CONFLICT(id) DO UPDATE SET
                source_id = excluded.source_id,
                title = excluded.title,
                media_type = excluded.media_type,
                rating = excluded.rating,
                synopsis = excluded.synopsis,
                language = excluded.language,
                tags = excluded.tags,
                updated_at = excluded.updated_at;
            """;

        command.Parameters.AddWithValue("$id", media.Id.Value);
        command.Parameters.AddWithValue("$sourceId", media.SourceId);
        command.Parameters.AddWithValue("$title", media.Title);
        command.Parameters.AddWithValue("$mediaType", media.MediaType.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$rating", (object?)media.Rating ?? DBNull.Value);
        command.Parameters.AddWithValue("$synopsis", (object?)media.Synopsis ?? DBNull.Value);
        command.Parameters.AddWithValue("$language", (object?)media.Language ?? DBNull.Value);
        command.Parameters.AddWithValue("$tags", tagsJson);
        command.Parameters.AddWithValue("$createdAt", media.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", media.UpdatedAtUtc.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MediaMetadata?> GetMediaAsync(MediaId mediaId, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                source_id,
                title,
                media_type,
                rating,
                synopsis,
                language,
                tags,
                created_at,
                updated_at
            FROM media
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", mediaId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadMedia(reader);
    }

    public async Task<IReadOnlyList<MediaMetadata>> ListMediaAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                source_id,
                title,
                media_type,
                rating,
                synopsis,
                language,
                tags,
                created_at,
                updated_at
            FROM media
            ORDER BY updated_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var results = new List<MediaMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadMedia(reader));
        }

        return results;
    }

    public async Task UpsertChaptersAsync(
        MediaId mediaId,
        IReadOnlyList<MediaChapterRecord> chapters,
        CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken) as SqliteTransaction
            ?? throw new InvalidOperationException("Failed to start SQLite transaction.");

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM media_chapters WHERE media_id = $mediaId;";
            deleteCommand.Parameters.AddWithValue("$mediaId", mediaId.Value);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var chapter in chapters)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO media_chapters (
                    id,
                    media_id,
                    chapter_number,
                    title,
                    published_at
                ) VALUES (
                    $id,
                    $mediaId,
                    $number,
                    $title,
                    $publishedAt
                );
                """;
            insertCommand.Parameters.AddWithValue("$id", chapter.ChapterId);
            insertCommand.Parameters.AddWithValue("$mediaId", mediaId.Value);
            insertCommand.Parameters.AddWithValue("$number", chapter.Number);
            insertCommand.Parameters.AddWithValue("$title", chapter.Title);
            insertCommand.Parameters.AddWithValue(
                "$publishedAt",
                chapter.PublishedAtUtc?.ToString("O") ?? (object)DBNull.Value);

            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MediaChapterRecord>> GetChaptersAsync(MediaId mediaId, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                media_id,
                chapter_number,
                title,
                published_at
            FROM media_chapters
            WHERE media_id = $mediaId
            ORDER BY chapter_number;
            """;
        command.Parameters.AddWithValue("$mediaId", mediaId.Value);

        var results = new List<MediaChapterRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadChapter(reader));
        }

        return results;
    }

    private static MediaMetadata ReadMedia(SqliteDataReader reader)
    {
        var tagsJson = reader.GetString(7);
        var tags = JsonSerializer.Deserialize<IReadOnlyList<string>>(tagsJson)
            ?? Array.Empty<string>();

        return new MediaMetadata(
            MediaId.Create(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            ParseMediaType(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            tags,
            DateTimeOffset.Parse(reader.GetString(8)),
            DateTimeOffset.Parse(reader.GetString(9)));
    }

    private static MediaChapterRecord ReadChapter(SqliteDataReader reader)
    {
        var publishedAt = reader.IsDBNull(4)
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(reader.GetString(4));

        return new MediaChapterRecord(
            reader.GetString(0),
            MediaId.Create(reader.GetString(1)),
            reader.GetInt32(2),
            reader.GetString(3),
            publishedAt);
    }

    private static MediaType ParseMediaType(string value)
    {
        return string.Equals(value, "video", StringComparison.OrdinalIgnoreCase)
            ? MediaType.Video
            : MediaType.Paged;
    }
}
