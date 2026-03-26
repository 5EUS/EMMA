using System.Reflection;
using Microsoft.Data.Sqlite;

namespace EMMA.Storage;

/// <summary>
/// Applies embedded SQL migrations for SQLite storage.
/// </summary>
public sealed class StorageInitializer(StorageOptions options)
{
    private readonly SqliteConnectionFactory _connectionFactory = new(options);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var dbPath = _connectionFactory.CreateConnection().DataSource;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await EnsureMigrationsTableAsync(connection, cancellationToken);
        await EnableWalAsync(connection, cancellationToken);

        var migrations = LoadMigrations();
        foreach (var migration in migrations)
        {
            if (await IsAppliedAsync(connection, migration.Name, cancellationToken))
            {
                continue;
            }

            await ApplyMigrationAsync(connection, migration, cancellationToken);
        }
    }

    private static async Task EnsureMigrationsTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var sql = """
            CREATE TABLE IF NOT EXISTS __emma_migrations (
                name TEXT PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnableWalAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static List<(string Name, string Sql)> LoadMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Sql.", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var migrations = new List<(string, string)>(resources.Count);
        foreach (var resource in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            migrations.Add((resource, sql));
        }

        return migrations;
    }

    private static async Task<bool> IsAppliedAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM __emma_migrations WHERE name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", name);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private static async Task ApplyMigrationAsync(
        SqliteConnection connection,
        (string Name, string Sql) migration,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, "BEGIN IMMEDIATE;", cancellationToken);

        try
        {
            if (await IsAppliedInCurrentTransactionAsync(connection, migration.Name, cancellationToken))
            {
                await ExecuteNonQueryAsync(connection, "COMMIT;", cancellationToken);
                return;
            }

            await ExecuteNonQueryAsync(connection, migration.Sql, cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO __emma_migrations (name, applied_at) VALUES ($name, $appliedAt);";
            command.Parameters.AddWithValue("$name", migration.Name);
            command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);

            await ExecuteNonQueryAsync(connection, "COMMIT;", cancellationToken);
        }
        catch
        {
            await ExecuteNonQueryAsync(connection, "ROLLBACK;", cancellationToken);
            throw;
        }
    }

    private static async Task<bool> IsAppliedInCurrentTransactionAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM __emma_migrations WHERE name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", name);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
