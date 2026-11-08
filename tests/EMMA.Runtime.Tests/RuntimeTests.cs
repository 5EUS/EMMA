using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Runtime.Tests;

/// <summary>Tests that the assembled runtime installs, serves, switches and removes real plugins.</summary>
public sealed class RuntimeTests
{
    private static readonly PluginId Fixture = PluginId.Create(RuntimeHarness.Plugin);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A freshly installed plugin is served without restarting the runtime.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task InstalledPluginIsServedImmediately()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.Runtime.InitializeAsync(Ct).ConfigureAwait(true);
            Outcome<InstalledPlugin> installed = await host.InstallAsync().ConfigureAwait(true);
            Assert.True(installed.IsSuccess, installed.Error?.Message);

            Outcome<NodePage> page = await host.Runtime.Catalog.SearchAsync(Fixture, TestData.TextQuery("series"), Ct).ConfigureAwait(true);

            Assert.True(page.IsSuccess, page.Error?.Message);
            Assert.NotEmpty(page.Value.Items);
        }
    }

    /// <summary>A plugin switched off stops answering at once, and answers again when switched back on.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DisablingAndEnablingTakeEffectAtOnce()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.Runtime.InitializeAsync(Ct).ConfigureAwait(true);
            await host.InstallAsync().ConfigureAwait(true);
            await host.Runtime.Catalog.SearchAsync(Fixture, TestData.TextQuery("series"), Ct).ConfigureAwait(true);

            await host.Runtime.SetStatusAsync(Fixture, PluginStatus.Disabled, Ct).ConfigureAwait(true);
            Outcome<NodePage> off = await host.Runtime.Catalog.SearchAsync(Fixture, TestData.TextQuery("series"), Ct).ConfigureAwait(true);
            await host.Runtime.SetStatusAsync(Fixture, PluginStatus.Enabled, Ct).ConfigureAwait(true);
            Outcome<NodePage> on = await host.Runtime.Catalog.SearchAsync(Fixture, TestData.TextQuery("series"), Ct).ConfigureAwait(true);

            Assert.Equal(ErrorCode.Unsupported, off.Error?.Code);
            Assert.True(on.IsSuccess);
        }
    }

    /// <summary>Removing a plugin deletes its record and its files, and it can no longer be reached.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UninstallRemovesRecordFilesAndAccess()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.Runtime.InitializeAsync(Ct).ConfigureAwait(true);
            await host.InstallAsync().ConfigureAwait(true);
            string directory = Path.Combine(host.Runtime.HostOptions.InstallRoot, RuntimeHarness.Plugin);
            Assert.True(Directory.Exists(directory));

            bool removed = await host.Runtime.UninstallAsync(Fixture, Ct).ConfigureAwait(true);
            bool again = await host.Runtime.UninstallAsync(Fixture, Ct).ConfigureAwait(true);
            Outcome<NodePage> page = await host.Runtime.Catalog.SearchAsync(Fixture, TestData.TextQuery("series"), Ct).ConfigureAwait(true);

            Assert.True(removed);
            Assert.False(again);
            Assert.False(Directory.Exists(directory));
            Assert.Null(await host.Runtime.Registry.GetAsync(Fixture, Ct).ConfigureAwait(true));
            Assert.Equal(ErrorCode.NotFound, page.Error?.Code);
        }
    }

    /// <summary>
    /// A value the user sets reaches the real plugin (a secret in clear, since only the plugin may see it), and a change
    /// is seen on the very next call without restarting anything.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SettingsReachThePluginAndChangesApplyAtOnce()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.Runtime.InitializeAsync(Ct).ConfigureAwait(true);
            await host.InstallAsync().ConfigureAwait(true);
            Assert.Equal("<none>", await ReadSettingAsync(host, "connection.token").ConfigureAwait(true));

            Outcome<IReadOnlyList<SettingState>> first = await host.Runtime.SetPluginSettingsAsync(Fixture, [new SettingChange("connection.token", ["abc"]), new SettingChange("connection.user", ["me"])], Ct).ConfigureAwait(true);
            Assert.True(first.IsSuccess, first.Error?.Message);
            Assert.Equal("abc", await ReadSettingAsync(host, "connection.token").ConfigureAwait(true));
            Assert.Equal("me", await ReadSettingAsync(host, "connection.user").ConfigureAwait(true));

            await host.Runtime.SetPluginSettingsAsync(Fixture, [new SettingChange("connection.token", ["def"])], Ct).ConfigureAwait(true);
            Assert.Equal("def", await ReadSettingAsync(host, "connection.token").ConfigureAwait(true));

            await host.Runtime.SetPluginSettingsAsync(Fixture, [new SettingChange("connection.user", [])], Ct).ConfigureAwait(true);
            Assert.Equal("<none>", await ReadSettingAsync(host, "connection.user").ConfigureAwait(true));
        }
    }

    /// <summary>Settings survive a restart, and what is on disk does not contain the secret.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SettingsSurviveARestartAndSecretsAreNotOnDiskInClear()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.Runtime.InitializeAsync(Ct).ConfigureAwait(true);
            await host.InstallAsync().ConfigureAwait(true);
            await host.Runtime.SetPluginSettingsAsync(Fixture, [new SettingChange("connection.token", ["s3cret-token-value"])], Ct).ConfigureAwait(true);

            await host.ReopenAsync().ConfigureAwait(true);
            await host.Runtime.InitializeAsync(Ct).ConfigureAwait(true);

            Assert.Equal("s3cret-token-value", await ReadSettingAsync(host, "connection.token").ConfigureAwait(true));
            byte[] database = await File.ReadAllBytesAsync(Path.Combine(host.DataDirectory, "emma.db"), Ct).ConfigureAwait(true);
            Assert.DoesNotContain("s3cret-token-value", System.Text.Encoding.UTF8.GetString(database), StringComparison.Ordinal);
        }
    }

    /// <summary>Removing a plugin removes what the user set for it, so a reinstall starts clean.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UninstallForgetsSettings()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.Runtime.InitializeAsync(Ct).ConfigureAwait(true);
            await host.InstallAsync().ConfigureAwait(true);
            await host.Runtime.SetPluginSettingsAsync(Fixture, [new SettingChange("connection.user", ["me"])], Ct).ConfigureAwait(true);

            await host.Runtime.UninstallAsync(Fixture, Ct).ConfigureAwait(true);
            await host.InstallAsync().ConfigureAwait(true);

            Assert.Equal("<none>", await ReadSettingAsync(host, "connection.user").ConfigureAwait(true));
        }
    }

    /// <summary>A disabled plugin cannot say what it declares, so its settings cannot be read or changed.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DisabledPluginHasNoSettingsToChange()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.Runtime.InitializeAsync(Ct).ConfigureAwait(true);
            await host.InstallAsync().ConfigureAwait(true);
            await host.Runtime.SetStatusAsync(Fixture, PluginStatus.Disabled, Ct).ConfigureAwait(true);

            Outcome<IReadOnlyList<SettingState>> read = await host.Runtime.GetPluginSettingsAsync(Fixture, Ct).ConfigureAwait(true);
            Outcome<IReadOnlyList<SettingState>> write = await host.Runtime.SetPluginSettingsAsync(Fixture, [new SettingChange("connection.user", ["me"])], Ct).ConfigureAwait(true);

            Assert.Equal(ErrorCode.Unsupported, read.Error?.Code);
            Assert.Equal(ErrorCode.Unsupported, write.Error?.Code);
        }
    }

    /// <summary>An unsigned package is refused under the production trust policy and leaves nothing behind.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UnsignedPackageIsRefused()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.Runtime.InitializeAsync(Ct).ConfigureAwait(true);

            Outcome<InstalledPlugin> installed = await host.InstallAsync(sign: false).ConfigureAwait(true);

            Assert.False(installed.IsSuccess);
            Assert.Empty(await host.Runtime.Registry.ListAsync(Ct).ConfigureAwait(true));
        }
    }

    /// <summary>A plugin whose stored manifest cannot be read is quarantined when the runtime starts.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UnreadableManifestIsQuarantinedAtStart()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.Runtime.InitializeAsync(Ct).ConfigureAwait(true);
            InstalledPlugin broken = new(Fixture, "1.0.0", PluginStatus.Enabled, "{ not a manifest", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            await host.Runtime.Registry.UpsertAsync(broken, Ct).ConfigureAwait(true);
            await host.ReopenAsync().ConfigureAwait(true);

            IReadOnlyList<PluginId> quarantined = await host.Runtime.InitializeAsync(Ct).ConfigureAwait(true);

            Assert.Equal([Fixture], quarantined);
            Assert.Equal(PluginStatus.Quarantined, (await host.Runtime.Registry.GetAsync(Fixture, Ct).ConfigureAwait(true))?.Status);
        }
    }

    /// <summary>A replacement gateway is used as given, so the pipelines run over it.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task GatewayCanBeReplaced()
    {
        FakePluginGateway fake = new() { OnSearch = (_, _) => Outcome.Success(TestData.RawPage(null, TestData.RawNode("s1"))) };
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.Runtime.DisposeAsync().ConfigureAwait(true);
            EmmaRuntime runtime = host.Open(options => options with { GatewayOverride = fake });
            await using (runtime.ConfigureAwait(true))
            {
                await runtime.InitializeAsync(Ct).ConfigureAwait(true);

                Outcome<NodePage> page = await runtime.Catalog.SearchAsync(TestData.Plugin, TestData.TextQuery("x"), Ct).ConfigureAwait(true);

                Assert.Same(fake, runtime.Gateway);
                Assert.Equal(TestData.Ref("s1"), Assert.Single(page.Value.Items).Ref);
            }
        }
    }

    private static async Task<string> ReadSettingAsync(RuntimeHarness host, string key)
    {
        Outcome<PluginNodePage> page = await host.Runtime.Gateway.SearchAsync(Fixture, TestData.TextQuery($"setting:{key}"), Ct).ConfigureAwait(true);
        Assert.True(page.IsSuccess, page.Error?.Message);
        return page.Value.Items.Single().Title;
    }
}
