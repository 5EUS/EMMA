using EMMA.Domain;
using EMMA.TestSupport;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.Storage.Tests;

/// <summary>A fresh, initialized database in a temporary directory for each test.</summary>
public abstract class StorageTestBase : IAsyncLifetime
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("emma-storage-test-");

    /// <summary>Gets the token that cancels when the running test is cancelled.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Gets the clock the stores use.</summary>
    protected FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Gets options pointing at this test's database file.</summary>
    protected StorageOptions Options => new(Path.Combine(this.directory.FullName, "emma.db"));

    /// <summary>Creates and migrates the database.</summary>
    /// <returns>A task that completes when the schema is current.</returns>
    public async ValueTask InitializeAsync() =>
        await new StorageInitializer(this.Options, this.Time).InitializeAsync(CancellationToken.None).ConfigureAwait(false);

    /// <summary>Releases pooled connections and removes the temporary directory.</summary>
    /// <returns>A completed task.</returns>
    public async ValueTask DisposeAsync()
    {
        await this.DisposeAsyncCore().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Builds a minimal node under <see cref="TestData.Plugin"/>.</summary>
    /// <param name="id">The local id.</param>
    /// <param name="parent">The parent's local id, if any.</param>
    /// <returns>The node.</returns>
    protected static Node MakeNode(string id, string? parent = null) => new(
        TestData.Ref(id),
        NodeKind.Create("series"),
        $"Title {id}",
        parent is null ? null : TestData.Ref(parent),
        null,
        []);

    /// <summary>Releases pooled connections and removes the temporary directory.</summary>
    /// <returns>A completed task.</returns>
    protected virtual ValueTask DisposeAsyncCore()
    {
        SqliteConnection.ClearAllPools();
        this.directory.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>Runs a constant SQL statement directly, bypassing the stores, and returns its first value.</summary>
    /// <param name="sql">A constant SQL text.</param>
    /// <returns>The first column of the first row.</returns>
    protected async Task<object?> ScalarAsync(string sql)
    {
        SqliteConnection connection = new($"Data Source={this.Options.DatabasePath}");
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            SqliteCommand command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
#pragma warning disable CA2100 // Test code: callers pass constants.
                command.CommandText = sql;
#pragma warning restore CA2100
                return await command.ExecuteScalarAsync().ConfigureAwait(false);
            }
        }
    }
}
