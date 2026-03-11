using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Storage;

/// <summary>
/// SQLite implementation for user library entries.
/// </summary>
public sealed class SqliteLibraryPort(StorageOptions options) : ILibraryPort
{
    private readonly SqliteConnectionFactory _connectionFactory = new(options);

    public async Task CreateLibraryAsync(string userId, string libraryName, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO libraries (id, name, created_at)
            VALUES ($id, $name, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", userId);
        command.Parameters.AddWithValue("$name", libraryName);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task NormalizeLegacyDefaultLibraryAsync(
        string canonicalUserId,
        CancellationToken cancellationToken)
    {
        _ = canonicalUserId;
        _ = cancellationToken;
        await Task.CompletedTask;
    }

    public async Task UpsertAsync(LibraryEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using (var createLibraryCommand = connection.CreateCommand())
        {
            createLibraryCommand.CommandText = """
                INSERT OR IGNORE INTO libraries (id, name, created_at)
                VALUES ($id, $name, $createdAt);
                """;
            createLibraryCommand.Parameters.AddWithValue("$id", entry.UserId);
            createLibraryCommand.Parameters.AddWithValue("$name", entry.UserId);
            createLibraryCommand.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            await createLibraryCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO library (
                id,
                media_id,
                user_id,
                added_at,
                source_id
            ) VALUES (
                $id,
                $mediaId,
                $userId,
                $addedAt,
                $sourceId
            )
            ON CONFLICT(id) DO UPDATE SET
                media_id = excluded.media_id,
                user_id = excluded.user_id,
                added_at = excluded.added_at,
                source_id = excluded.source_id;
            """;

        command.Parameters.AddWithValue("$id", entry.EntryId);
        command.Parameters.AddWithValue("$mediaId", entry.MediaId.Value);
        command.Parameters.AddWithValue("$userId", entry.UserId);
        command.Parameters.AddWithValue("$addedAt", entry.AddedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$sourceId", entry.SourceId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryEntry>> GetLibraryAsync(string userId, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, media_id, user_id, added_at, source_id
            FROM library
            WHERE user_id = $userId
            ORDER BY added_at DESC;
            """;
        command.Parameters.AddWithValue("$userId", userId);

        var results = new List<LibraryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new LibraryEntry(
                reader.GetString(0),
                MediaId.Create(reader.GetString(1)),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                reader.GetString(4)));
        }

        return results;
    }

    public async Task<IReadOnlyList<string>> ListLibrariesAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id FROM libraries
            UNION
            SELECT DISTINCT user_id FROM library
            ORDER BY 1;
            """;

        var results = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(value))
            {
                results.Add(value);
            }
        }

        return results;
    }

    public async Task RemoveAsync(string userId, MediaId mediaId, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM library WHERE user_id = $userId AND media_id = $mediaId;";
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$mediaId", mediaId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
