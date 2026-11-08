using EMMA.Domain;
using EMMA.TestSupport;

using Microsoft.Data.Sqlite;

using Xunit;

namespace EMMA.Storage.Tests;

/// <summary>Tests for <see cref="SqlitePluginRegistry"/>.</summary>
public sealed class SqlitePluginRegistryTests : StorageTestBase
{
    private static DateTimeOffset Moment => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private SqlitePluginRegistry Registry => new(this.Options);

    /// <summary>A plugin comes back as stored, signed or not.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RoundTrips()
    {
        InstalledPlugin signed = Plugin(TestData.Plugin, "1.0.0") with { Signer = "repo/key-1" };
        InstalledPlugin unsigned = Plugin(TestData.OtherPlugin, "0.1.0");

        await this.Registry.UpsertAsync(signed, Ct);
        await this.Registry.UpsertAsync(unsigned, Ct);

        Assert.Equal(signed, await this.Registry.GetAsync(TestData.Plugin, Ct));
        Assert.Equal(unsigned, await this.Registry.GetAsync(TestData.OtherPlugin, Ct));
        Assert.Null(await this.Registry.GetAsync(PluginId.Create("com.example.nope"), Ct));
    }

    /// <summary>Upserting again is an upgrade: one record per plugin.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UpsertReplacesTheRecord()
    {
        await this.Registry.UpsertAsync(Plugin(TestData.Plugin, "1.0.0"), Ct);
        await this.Registry.UpsertAsync(Plugin(TestData.Plugin, "1.1.0"), Ct);

        Assert.Equal("1.1.0", (await this.Registry.GetAsync(TestData.Plugin, Ct))!.Version);
        Assert.Single(await this.Registry.ListAsync(Ct));
    }

    /// <summary>The list is ordered by id.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ListsByIdOrder()
    {
        await this.Registry.UpsertAsync(Plugin(TestData.Plugin, "1.0.0"), Ct);
        await this.Registry.UpsertAsync(Plugin(TestData.OtherPlugin, "1.0.0"), Ct);

        Assert.Equal(
            [TestData.OtherPlugin, TestData.Plugin],
            (await this.Registry.ListAsync(Ct)).Select(plugin => plugin.Id));
    }

    /// <summary>Changing the status updates the timestamp, and reports whether the plugin existed.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SetStatusReportsWhetherThePluginExisted()
    {
        await this.Registry.UpsertAsync(Plugin(TestData.Plugin, "1.0.0"), Ct);
        DateTimeOffset later = Moment.AddHours(1);

        Assert.True(await this.Registry.SetStatusAsync(TestData.Plugin, PluginStatus.Quarantined, later, Ct));
        Assert.False(await this.Registry.SetStatusAsync(PluginId.Create("com.example.nope"), PluginStatus.Disabled, later, Ct));

        InstalledPlugin? loaded = await this.Registry.GetAsync(TestData.Plugin, Ct);
        Assert.Equal(PluginStatus.Quarantined, loaded!.Status);
        Assert.Equal(later, loaded.UpdatedAt);
    }

    /// <summary>Removing reports whether the plugin existed.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RemoveReportsWhetherThePluginExisted()
    {
        await this.Registry.UpsertAsync(Plugin(TestData.Plugin, "1.0.0"), Ct);

        Assert.True(await this.Registry.RemoveAsync(TestData.Plugin, Ct));
        Assert.False(await this.Registry.RemoveAsync(TestData.Plugin, Ct));
    }

    /// <summary>The database refuses an unknown status.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task TheDatabaseRejectsAnUnknownStatus() =>
        Assert.ThrowsAsync<SqliteException>(() => this.Registry.UpsertAsync(Plugin(TestData.Plugin, "1.0.0") with { Status = (PluginStatus)7 }, Ct));

    private static InstalledPlugin Plugin(PluginId id, string version) =>
        new(id, version, PluginStatus.Enabled, "{}", null, Moment, Moment);
}
