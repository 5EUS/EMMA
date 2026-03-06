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
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken) as Microsoft.Data.Sqlite.SqliteTransaction
            ?? throw new InvalidOperationException("Failed to start SQLite transaction.");

        await using (var ensureCanonicalLibrary = connection.CreateCommand())
        {
            ensureCanonicalLibrary.Transaction = transaction;
            ensureCanonicalLibrary.CommandText = """
                INSERT OR IGNORE INTO libraries (id, name, created_at)
                VALUES ($id, $name, $createdAt);
                """;
            ensureCanonicalLibrary.Parameters.AddWithValue("$id", canonicalUserId);
            ensureCanonicalLibrary.Parameters.AddWithValue("$name", "Library");
            ensureCanonicalLibrary.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            await ensureCanonicalLibrary.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var migrateEntries = connection.CreateCommand())
        {
            migrateEntries.Transaction = transaction;
            migrateEntries.CommandText = """
                INSERT OR IGNORE INTO library (id, media_id, user_id, added_at, source_id)
                SELECT $canonicalUserId || ':' || media_id, media_id, $canonicalUserId, added_at, source_id
                FROM library
                WHERE lower(user_id) = 'default'
                   OR lower(user_id) = 'library::default';
                """;
            migrateEntries.Parameters.AddWithValue("$canonicalUserId", canonicalUserId);
            await migrateEntries.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteLegacyEntries = connection.CreateCommand())
        {
            deleteLegacyEntries.Transaction = transaction;
            deleteLegacyEntries.CommandText = """
                DELETE FROM library
                WHERE lower(user_id) = 'default'
                   OR lower(user_id) = 'library::default';
                """;
            await deleteLegacyEntries.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteLegacyLibraryNames = connection.CreateCommand())
        {
            deleteLegacyLibraryNames.Transaction = transaction;
            deleteLegacyLibraryNames.CommandText = """
                DELETE FROM libraries
                WHERE lower(id) = 'default'
                   OR lower(id) = 'library::default';
                """;
            await deleteLegacyLibraryNames.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
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
