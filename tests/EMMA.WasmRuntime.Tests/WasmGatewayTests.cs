using EMMA.Domain;
using EMMA.PluginHost.Manifest;
using EMMA.PluginHost.Runtime;

using Xunit;

namespace EMMA.WasmRuntime.Tests;

/// <summary>Tests for the Wasm gateway end to end: a signed package is installed, verified again at load, and run in the native host.</summary>
/// <param name="runtime">The shared native runtime.</param>
public sealed class WasmGatewayTests(RuntimeFixture runtime) : IClassFixture<RuntimeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A signed fixture installs, loads and serves search, children and resolve through the gateway port.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task AnInstalledSignedPluginServesThroughTheGatewayPort()
    {
        return this.WithHostAsync(
            maxTraps: 5,
            async host =>
            {
                Outcome<InstalledPlugin> installed = await host.InstallAsync(await GatewayHarness.PackageAsync("fixture").ConfigureAwait(true), Ct).ConfigureAwait(true);
                Assert.True(installed.IsSuccess, installed.Error?.Message);
                WasmPluginGateway gateway = host.Gateway;

                Outcome<PluginNodePage> search = await gateway.SearchAsync(GatewayHarness.Id, Text("series"), Ct).ConfigureAwait(true);
                Outcome<PluginNodePage> children = await gateway.ChildrenAsync(GatewayHarness.Id, LocalNodeId.Create("s1"), new PageRequest(null, 2), Ct).ConfigureAwait(true);
                Outcome<IReadOnlyList<Asset>> assets = await gateway.ResolveAsync(GatewayHarness.Id, new ResolveRequest(LocalNodeId.Create("s1-c1-p1"), AssetRole.Create("image"), []), Ct).ConfigureAwait(true);
                Outcome<Chunk> bytes = await gateway.ReadAsync(GatewayHarness.Id, "tok-1", new ByteRange(0, 4), Ct).ConfigureAwait(true);
                Outcome<PluginInfo> info = await gateway.DescribeAsync(GatewayHarness.Id, Ct).ConfigureAwait(true);

                Assert.Equal(3, search.Value.Items.Count);
                Assert.Equal(["Chapter 1", "Chapter 2"], children.Value.Items.Select(node => node.Title));
                Assert.IsType<RemoteDelivery>(assets.Value.Single().Delivery);
                Assert.Equal(new byte[] { 0, 1, 2, 3 }, bytes.Value.Bytes.ToArray());
                Assert.NotNull(info.Value.FindKind(NodeKind.Create("recipe")));
            });
    }

    /// <summary>A plugin that is not installed, or is disabled, is refused without running anything.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task MissingAndDisabledPluginsAreRefused()
    {
        return this.WithHostAsync(
            maxTraps: 5,
            async host =>
            {
                Outcome<PluginNodePage> missing = await host.Gateway.SearchAsync(GatewayHarness.Id, Text("series"), Ct).ConfigureAwait(true);
                await host.InstallAsync(await GatewayHarness.PackageAsync("fixture").ConfigureAwait(true), Ct).ConfigureAwait(true);
                await host.Registry.SetStatusAsync(GatewayHarness.Id, PluginStatus.Disabled, host.Time.GetUtcNow(), Ct).ConfigureAwait(true);

                Outcome<PluginNodePage> disabled = await host.Gateway.SearchAsync(GatewayHarness.Id, Text("series"), Ct).ConfigureAwait(true);

                Assert.Equal(ErrorCode.NotFound, missing.Error?.Code);
                Assert.Equal(ErrorCode.Unavailable, disabled.Error?.Code);
            });
    }

    /// <summary>A component changed on disk after install is refused and the plugin is quarantined.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ATamperedComponentIsRefusedAndQuarantined()
    {
        return this.WithHostAsync(
            maxTraps: 5,
            async host =>
            {
                await host.InstallAsync(await GatewayHarness.PackageAsync("fixture").ConfigureAwait(true), Ct).ConfigureAwait(true);
                await File.WriteAllBytesAsync(host.InstalledEntrypoint, await File.ReadAllBytesAsync(NativeArtifacts.Plugin("fixture-assist"), Ct).ConfigureAwait(true), Ct).ConfigureAwait(true);

                Outcome<PluginNodePage> result = await host.Gateway.SearchAsync(GatewayHarness.Id, Text("series"), Ct).ConfigureAwait(true);

                Assert.Equal(ErrorCode.Unauthorized, result.Error?.Code);
                Assert.Equal(PluginStatus.Quarantined, (await host.Registry.GetAsync(GatewayHarness.Id, Ct).ConfigureAwait(true))!.Status);
            });
    }

    /// <summary>A manifest that lists kinds the component does not declare is refused at load.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ManifestKindsThatDisagreeWithDescribeAreRefused()
    {
        return this.WithHostAsync(
            maxTraps: 5,
            async host =>
            {
                byte[] package = await GatewayHarness.PackageAsync("fixture", adjust: manifest => manifest with { Kinds = [NodeKind.Create("series"), NodeKind.Create("novel")] }).ConfigureAwait(true);
                Outcome<InstalledPlugin> installed = await host.InstallAsync(package, Ct).ConfigureAwait(true);
                Assert.True(installed.IsSuccess, installed.Error?.Message);

                Outcome<PluginNodePage> result = await host.Gateway.SearchAsync(GatewayHarness.Id, Text("series"), Ct).ConfigureAwait(true);

                Assert.Equal(ErrorCode.InvalidArgument, result.Error?.Code);
                Assert.Contains("kinds", result.Error!.Message, StringComparison.Ordinal);
            });
    }

    /// <summary>The limits and permissions from the signed manifest reach the native host.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ManifestLimitsAndPermissionsAreEnforced()
    {
        return this.WithHostAsync(
            maxTraps: 5,
            async host =>
            {
                byte[] package = await GatewayHarness.PackageAsync("fixture", adjust: manifest => manifest with { Limits = new ManifestLimits(200, 128) }).ConfigureAwait(true);
                await host.InstallAsync(package, Ct).ConfigureAwait(true);

                Outcome<PluginNodePage> spin = await host.Gateway.SearchAsync(GatewayHarness.Id, Text("spin"), Ct).ConfigureAwait(true);
                Outcome<PluginNodePage> http = await host.Gateway.SearchAsync(GatewayHarness.Id, Text("http:https://evil.example.net/x"), Ct).ConfigureAwait(true);

                Assert.False(spin.IsSuccess);
                Assert.Equal(ErrorCode.InvalidArgument, http.Error?.Code);
            });
    }

    /// <summary>User settings reach the plugin, and plugin logs are forwarded with the plugin id.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task SettingsAndLogsFlowThroughTheHost()
    {
        return this.WithHostAsync(
            maxTraps: 5,
            async host =>
            {
                host.Settings.With(GatewayHarness.Id, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["connection.user"] = ["alice"] });
                await host.InstallAsync(await GatewayHarness.PackageAsync("fixture").ConfigureAwait(true), Ct).ConfigureAwait(true);

                Outcome<PluginNodePage> user = await host.Gateway.SearchAsync(GatewayHarness.Id, Text("setting:connection.user"), Ct).ConfigureAwait(true);
                await host.Gateway.SearchAsync(GatewayHarness.Id, Text("log"), Ct).ConfigureAwait(true);

                Assert.Equal("alice", user.Value.Items.Single().Title);
                Assert.Equal(["debug line", "info line", "error line"], host.Log.Written.Select(item => item.Entry.Message));
                Assert.All(host.Log.Written, item => Assert.Equal(GatewayHarness.Id, item.Plugin));
            });
    }

    /// <summary>A plugin that traps on every call is quarantined after the configured number, and a good call resets the count.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task RepeatedTrapsQuarantineThePlugin()
    {
        return this.WithHostAsync(
            maxTraps: 3,
            async host =>
            {
                await host.InstallAsync(await GatewayHarness.PackageAsync("fixture").ConfigureAwait(true), Ct).ConfigureAwait(true);

                await host.Gateway.SearchAsync(GatewayHarness.Id, Text("trap"), Ct).ConfigureAwait(true);
                await host.Gateway.SearchAsync(GatewayHarness.Id, Text("trap"), Ct).ConfigureAwait(true);
                await host.Gateway.SearchAsync(GatewayHarness.Id, Text("series"), Ct).ConfigureAwait(true);
                await host.Gateway.SearchAsync(GatewayHarness.Id, Text("trap"), Ct).ConfigureAwait(true);
                await host.Gateway.SearchAsync(GatewayHarness.Id, Text("trap"), Ct).ConfigureAwait(true);
                PluginStatus beforeThird = (await host.Registry.GetAsync(GatewayHarness.Id, Ct).ConfigureAwait(true))!.Status;
                await host.Gateway.SearchAsync(GatewayHarness.Id, Text("trap"), Ct).ConfigureAwait(true);

                Outcome<PluginNodePage> after = await host.Gateway.SearchAsync(GatewayHarness.Id, Text("series"), Ct).ConfigureAwait(true);

                Assert.Equal(PluginStatus.Enabled, beforeThird);
                Assert.Equal(PluginStatus.Quarantined, (await host.Registry.GetAsync(GatewayHarness.Id, Ct).ConfigureAwait(true))!.Status);
                Assert.Equal(ErrorCode.Unavailable, after.Error?.Code);
            });
    }

    /// <summary>Unloading closes the plugin so the next call loads it again with fresh settings.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task UnloadingPicksUpChangedSettings()
    {
        return this.WithHostAsync(
            maxTraps: 5,
            async host =>
            {
                await host.InstallAsync(await GatewayHarness.PackageAsync("fixture").ConfigureAwait(true), Ct).ConfigureAwait(true);
                host.Settings.With(GatewayHarness.Id, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["connection.user"] = ["alice"] });
                Outcome<PluginNodePage> first = await host.Gateway.SearchAsync(GatewayHarness.Id, Text("setting:connection.user"), Ct).ConfigureAwait(true);
                host.Settings.With(GatewayHarness.Id, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["connection.user"] = ["bob"] });
                Outcome<PluginNodePage> stale = await host.Gateway.SearchAsync(GatewayHarness.Id, Text("setting:connection.user"), Ct).ConfigureAwait(true);

                await host.Gateway.UnloadAsync(GatewayHarness.Id, Ct).ConfigureAwait(true);
                Outcome<PluginNodePage> fresh = await host.Gateway.SearchAsync(GatewayHarness.Id, Text("setting:connection.user"), Ct).ConfigureAwait(true);

                Assert.Equal("alice", first.Value.Items.Single().Title);
                Assert.Equal("alice", stale.Value.Items.Single().Title);
                Assert.Equal("bob", fresh.Value.Items.Single().Title);
            });
    }

    private static Query Text(string text) => new(text, [], [], null, null, null);

    private async Task WithHostAsync(int maxTraps, Func<GatewayHarness, Task> test)
    {
        GatewayHarness host = new(runtime.Host, maxTraps: maxTraps);
        await using (host.ConfigureAwait(true))
        {
            await test(host).ConfigureAwait(true);
        }
    }
}
