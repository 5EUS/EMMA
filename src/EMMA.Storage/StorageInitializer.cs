using Microsoft.Data.Sqlite;

namespace EMMA.Storage;

/// <summary>
/// Creates the database and brings its schema up to date. Safe to call on every start and
/// from several processes at once: each migration runs in its own immediate transaction and
/// re-checks that it has not already been applied. A database written by a newer EMMA is
/// refused rather than silently misread.
/// </summary>
/// <param name="options">Where the database lives.</param>
/// <param name="time">The time source recorded with each applied migration.</param>
public sealed class StorageInitializer(StorageOptions options, TimeProvider time)
{
    private const string CreateVersionTable = @"
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version    INTEGER NOT NULL PRIMARY KEY,
            name       TEXT    NOT NULL,
            applied_at INTEGER NOT NULL
        ) STRICT;
    ";

    private readonly SqliteDatabase database = new(options);
    private readonly string? directory = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath));

    /// <summary>Creates the database file if needed and applies every pending migration.</summary>
    /// <param name="cancellationToken">Cancels the initialization.</param>
    /// <returns>A task that completes when the schema is current.</returns>
    /// <exception cref="InvalidOperationException">The database was written by a newer version of EMMA.</exception>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(this.directory))
        {
            Directory.CreateDirectory(this.directory);
        }

        SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await SqliteDatabase.ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", null, cancellationToken).ConfigureAwait(false);
            await SqliteDatabase.ExecuteAsync(connection, CreateVersionTable, null, cancellationToken).ConfigureAwait(false);

            int latest = Migrations.LatestVersion;
            int current = await CurrentVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (current > latest)
            {
                throw new InvalidOperationException(
                    $"The database is at schema version {current} but this build only knows up to {latest}. Update EMMA.");
            }

            foreach (Migration migration in (await Migrations.LoadAsync(cancellationToken).ConfigureAwait(false)).Where(candidate => candidate.Version > current))
            {
                await this.ApplyAsync(connection, migration, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> CurrentVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private async Task ApplyAsync(SqliteConnection connection, Migration migration, CancellationToken cancellationToken)
    {
        SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            // Another process may have applied it between our version check and acquiring the write lock.
            bool applied = await SqliteDatabase.ExecuteAsync(
                connection,
                "INSERT OR IGNORE INTO schema_migrations (version, name, applied_at) VALUES ($version, $name, $appliedAt);",
                parameters =>
                {
                    parameters.Bind("$version", migration.Version);
                    parameters.Bind("$name", migration.Name);
                    parameters.Bind("$appliedAt", SqliteValues.ToStored(time.GetUtcNow()));
                },
                cancellationToken).ConfigureAwait(false) > 0;

            if (applied)
            {
                await SqliteDatabase.ExecuteAsync(connection, migration.Sql, null, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
