using EMMA.Application.Ports;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Storage.Tests;

/// <summary>Tests for <see cref="SqliteAssetCacheIndex"/>.</summary>
public sealed class SqliteAssetCacheIndexTests : StorageTestBase
{
    private SqliteAssetCacheIndex Index => new(this.Options);

    /// <summary>An entry round-trips, and writing the same key replaces it.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UpsertRoundTripsAndReplaces()
    {
        await this.Index.UpsertAsync(Record("k1", "f1"), Ct);
        await this.Index.UpsertAsync(Record("k1", "f2"), Ct);

        AssetCacheRecord loaded = Assert.Single(await this.Index.LoadAsync(Ct));
        Assert.Equal(Record("k1", "f2"), loaded);
    }

    /// <summary>Touching changes the last-use time only, and unknown keys are ignored.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TouchUpdatesLastUse()
    {
        await this.Index.UpsertAsync(Record("k1", "f1"), Ct);
        DateTimeOffset later = this.Time.GetUtcNow().AddHours(3);

        await this.Index.TouchAsync(new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal) { ["k1"] = later, ["nope"] = later }, Ct);

        AssetCacheRecord loaded = Assert.Single(await this.Index.LoadAsync(Ct));
        Assert.Equal(later, loaded.LastUsedAt);
        Assert.Equal("f1", loaded.File);
    }

    /// <summary>Removing and clearing forget entries.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RemoveAndClearForgetEntries()
    {
        await this.Index.UpsertAsync(Record("k1", "f1"), Ct);
        await this.Index.UpsertAsync(Record("k2", "f2"), Ct);
        await this.Index.UpsertAsync(Record("k3", "f3"), Ct);

        await this.Index.RemoveAsync(["k1", "missing"], Ct);
        Assert.Equal(["k2", "k3"], (await this.Index.LoadAsync(Ct)).Select(record => record.Key).Order(StringComparer.Ordinal));

        await this.Index.ClearAsync(Ct);
        Assert.Empty(await this.Index.LoadAsync(Ct));
    }

    private static AssetCacheRecord Record(string key, string file) =>
        new(key, TestData.Ref("n"), file, "image/png", 4, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero));
}
