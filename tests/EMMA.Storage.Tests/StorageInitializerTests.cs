using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.Storage.Tests;

/// <summary>Tests for <see cref="StorageInitializer"/> and the migrations.</summary>
public sealed class StorageInitializerTests : StorageTestBase
{
    /// <summary>Every table in the schema exists after initialization.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CreatesTheSchema()
    {
        long tables = (long)(await this.ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('nodes','progress','library','download_jobs','schema_migrations','plugins','trusted_delegation_versions')").ConfigureAwait(true))!;

        Assert.Equal(7, tables);
    }

    /// <summary>Running again changes nothing and records each migration once, with the clock's time.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task IsIdempotentAndRecordsTheClock()
    {
        await new StorageInitializer(this.Options, new FakeTimeProvider(DateTimeOffset.UnixEpoch)).InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal((long)Migrations.RegisteredScriptNames.Count, await this.ScalarAsync("SELECT COUNT(*) FROM schema_migrations").ConfigureAwait(true));
        Assert.Equal(this.Time.GetUtcNow().ToUnixTimeMilliseconds(), await this.ScalarAsync("SELECT applied_at FROM schema_migrations WHERE version = 1").ConfigureAwait(true));
        Assert.Equal("initial", await this.ScalarAsync("SELECT name FROM schema_migrations WHERE version = 1").ConfigureAwait(true));
    }

    /// <summary>A database in a directory that does not exist yet is created, including parents.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CreatesMissingDirectories()
    {
        string nested = Path.Combine(Path.GetDirectoryName(this.Options.DatabasePath)!, "a", "b", "emma.db");

        await new StorageInitializer(new StorageOptions(nested), this.Time).InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.True(File.Exists(nested));
    }

    /// <summary>The database uses write-ahead logging so readers do not block the writer.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task EnablesWriteAheadLogging() =>
        Assert.Equal("wal", await this.ScalarAsync("PRAGMA journal_mode").ConfigureAwait(true));

    /// <summary>A database from a newer EMMA is refused, naming both versions.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RefusesADatabaseFromANewerVersion()
    {
        await this.ScalarAsync("INSERT INTO schema_migrations (version, name, applied_at) VALUES (999, 'future', 0)").ConfigureAwait(true);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new StorageInitializer(this.Options, this.Time).InitializeAsync(CancellationToken.None)).ConfigureAwait(true);

        Assert.Contains("999", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Several processes starting at once leave exactly one record of each migration.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ConcurrentInitializationIsSafe()
    {
        StorageOptions fresh = new(Path.Combine(Path.GetDirectoryName(this.Options.DatabasePath)!, "concurrent.db"));

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            new StorageInitializer(fresh, this.Time).InitializeAsync(CancellationToken.None))).ConfigureAwait(true);

        SqliteConnection connection = new($"Data Source={fresh.DatabasePath}");
        await using (connection.ConfigureAwait(true))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            SqliteCommand command = connection.CreateCommand();
            await using (command.ConfigureAwait(true))
            {
                command.CommandText = "SELECT COUNT(*) FROM schema_migrations";
                Assert.Equal((long)Migrations.RegisteredScriptNames.Count, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
            }
        }
    }

    /// <summary>Every embedded script is registered, versions are contiguous from 1, and each script has content.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task EveryScriptIsRegisteredInOrder()
    {
        IReadOnlyList<Migration> migrations = await Migrations.LoadAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(Migrations.EmbeddedScriptNames(), Migrations.RegisteredScriptNames);
        Assert.Equal(Enumerable.Range(1, migrations.Count), migrations.Select(migration => migration.Version));
        Assert.All(migrations, migration => Assert.False(string.IsNullOrWhiteSpace(migration.Sql)));
        Assert.Equal(migrations[^1].Version, Migrations.LatestVersion);
    }

    /// <summary>The engine itself rejects a value of the wrong type, because the tables are STRICT.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TablesAreStrict()
    {
        SqliteException error = await Assert.ThrowsAsync<SqliteException>(() => this.ScalarAsync(
            "INSERT INTO progress (user_id, plugin_id, local_id, unit, value, total, completed, updated_at) VALUES ('u','p','l','page','not a number',NULL,0,0)")).ConfigureAwait(true);

        Assert.Equal(19, error.SqliteErrorCode);
        Assert.NotEmpty(error.Message.ToString(CultureInfo.InvariantCulture));
    }
}
