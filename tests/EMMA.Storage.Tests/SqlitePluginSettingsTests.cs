using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.TestSupport;

using Microsoft.Data.Sqlite;

using Xunit;

namespace EMMA.Storage.Tests;

/// <summary>Tests for <see cref="SqlitePluginSettings"/>.</summary>
public sealed class SqlitePluginSettingsTests : StorageTestBase
{
    private static DateTimeOffset Moment => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private SqlitePluginRegistry Registry => new(this.Options);

    /// <summary>Values come back as stored, several in the order given.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ValuesRoundTripInOrder()
    {
        SqlitePluginSettings settings = await this.StoreAsync(new FakeSecretBox());

        await settings.ApplyAsync(
            TestData.Plugin,
            [
                new SettingWrite("quality", ["high"], IsSecret: false),
                new SettingWrite("languages", ["ja", "en", "de"], IsSecret: false),
            ],
            Ct);

        IReadOnlyDictionary<string, IReadOnlyList<string>> values = await settings.GetAsync(TestData.Plugin, Ct);
        Assert.Equal(["high"], values["quality"]);
        Assert.Equal(["ja", "en", "de"], values["languages"]);
        Assert.Equal(["languages", "quality"], (await settings.ListAsync(TestData.Plugin, Ct)).Select(setting => setting.Key));
    }

    /// <summary>A write replaces what was there, and a write with no values removes the setting.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task WritingReplacesAndEmptyClears()
    {
        SqlitePluginSettings settings = await this.StoreAsync(new FakeSecretBox());
        await settings.ApplyAsync(TestData.Plugin, [new SettingWrite("languages", ["ja", "en"], IsSecret: false), new SettingWrite("quality", ["high"], IsSecret: false)], Ct);

        await settings.ApplyAsync(TestData.Plugin, [new SettingWrite("languages", ["de"], IsSecret: false), new SettingWrite("quality", [], IsSecret: false)], Ct);

        IReadOnlyDictionary<string, IReadOnlyList<string>> values = await settings.GetAsync(TestData.Plugin, Ct);
        Assert.Equal(["de"], values["languages"]);
        Assert.False(values.ContainsKey("quality"));
    }

    /// <summary>A secret is stored encrypted, listed as set without its value, and given back in clear only to the host.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SecretsAreEncryptedAtRestAndNeverListed()
    {
        SqlitePluginSettings settings = await this.StoreAsync(new FakeSecretBox());

        await settings.ApplyAsync(TestData.Plugin, [new SettingWrite("token", ["s3cret-value"], IsSecret: true)], Ct);

        IReadOnlyList<string> raw = await new SqliteDatabase(this.Options).QueryAsync("SELECT value FROM plugin_settings WHERE secret = 1;", null, reader => reader.GetString(0), Ct);
        Assert.Single(raw);
        Assert.DoesNotContain("s3cret-value", raw[0], StringComparison.Ordinal);
        StoredSetting listed = Assert.Single(await settings.ListAsync(TestData.Plugin, Ct));
        Assert.True(listed.IsSecret);
        Assert.Empty(listed.Values);
        Assert.Equal(["s3cret-value"], (await settings.GetAsync(TestData.Plugin, Ct))["token"]);
    }

    /// <summary>A secret the box can no longer open reads as not set, so the plugin still starts.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SecretMadeWithAnotherKeyReadsAsNotSet()
    {
        SqlitePluginSettings writer = await this.StoreAsync(new FakeSecretBox("old-key"));
        await writer.ApplyAsync(TestData.Plugin, [new SettingWrite("token", ["abc"], IsSecret: true), new SettingWrite("user", ["me"], IsSecret: false)], Ct);

        SqlitePluginSettings reader = new(this.Options, new FakeSecretBox("new-key"), this.Time);

        IReadOnlyDictionary<string, IReadOnlyList<string>> values = await reader.GetAsync(TestData.Plugin, Ct);
        Assert.Equal(["user"], values.Keys);
        Assert.Equal(["user"], (await reader.ListAsync(TestData.Plugin, Ct)).Select(setting => setting.Key));
    }

    /// <summary>A secret cannot be copied to another plugin's setting: the context is part of what makes it open.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SecretIsTiedToItsPluginAndKey()
    {
        SqlitePluginSettings settings = await this.StoreAsync(new FakeSecretBox());
        await this.Registry.UpsertAsync(Plugin(TestData.OtherPlugin), Ct);
        await settings.ApplyAsync(TestData.Plugin, [new SettingWrite("token", ["abc"], IsSecret: true)], Ct);

        await new SqliteDatabase(this.Options).ExecuteAsync(
            "INSERT INTO plugin_settings (plugin_id, setting_key, position, value, secret, updated_at) SELECT $other, setting_key, position, value, secret, updated_at FROM plugin_settings WHERE plugin_id = $plugin;",
            parameters =>
            {
                parameters.AddWithValue("$other", TestData.OtherPlugin.Value);
                parameters.AddWithValue("$plugin", TestData.Plugin.Value);
            },
            Ct);

        Assert.Empty(await settings.GetAsync(TestData.OtherPlugin, Ct));
    }

    /// <summary>Settings belong to one plugin.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task PluginsDoNotSeeEachOthersSettings()
    {
        SqlitePluginSettings settings = await this.StoreAsync(new FakeSecretBox());
        await this.Registry.UpsertAsync(Plugin(TestData.OtherPlugin), Ct);

        await settings.ApplyAsync(TestData.Plugin, [new SettingWrite("quality", ["high"], IsSecret: false)], Ct);

        Assert.Empty(await settings.GetAsync(TestData.OtherPlugin, Ct));
    }

    /// <summary>Removing a plugin removes its settings, secrets included.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UninstallingDeletesSettings()
    {
        SqlitePluginSettings settings = await this.StoreAsync(new FakeSecretBox());
        await settings.ApplyAsync(TestData.Plugin, [new SettingWrite("token", ["abc"], IsSecret: true)], Ct);

        await this.Registry.RemoveAsync(TestData.Plugin, Ct);

        IReadOnlyList<long> rows = await new SqliteDatabase(this.Options).QueryAsync("SELECT COUNT(*) FROM plugin_settings;", null, reader => reader.GetInt64(0), Ct);
        Assert.Equal(0, rows[0]);
    }

    /// <summary>Upgrading a plugin keeps what the user set.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UpgradingKeepsSettings()
    {
        SqlitePluginSettings settings = await this.StoreAsync(new FakeSecretBox());
        await settings.ApplyAsync(TestData.Plugin, [new SettingWrite("quality", ["high"], IsSecret: false)], Ct);

        await this.Registry.UpsertAsync(Plugin(TestData.Plugin) with { Version = "2.0.0" }, Ct);

        Assert.Equal(["high"], (await settings.GetAsync(TestData.Plugin, Ct))["quality"]);
    }

    /// <summary>Settings cannot be stored for a plugin that is not installed.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SettingsForAnUnknownPluginAreRefused()
    {
        SqlitePluginSettings settings = await this.StoreAsync(new FakeSecretBox());

        await Assert.ThrowsAsync<SqliteException>(() => settings.ApplyAsync(TestData.OtherPlugin, [new SettingWrite("quality", ["high"], IsSecret: false)], Ct));
    }

    private static InstalledPlugin Plugin(PluginId id) => new(id, "1.0.0", PluginStatus.Enabled, "{}", null, Moment, Moment);

    private async Task<SqlitePluginSettings> StoreAsync(ISecretBox box)
    {
        await this.Registry.UpsertAsync(Plugin(TestData.Plugin), Ct).ConfigureAwait(true);
        return new SqlitePluginSettings(this.Options, box, this.Time);
    }
}
