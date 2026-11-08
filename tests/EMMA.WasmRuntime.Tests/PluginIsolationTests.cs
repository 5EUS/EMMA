using EMMA.Domain;

using Xunit;

namespace EMMA.WasmRuntime.Tests;

/// <summary>Tests for plugin isolation through the native host.</summary>
/// <param name="runtime">The shared native runtime.</param>
public sealed class PluginIsolationTests(RuntimeFixture runtime) : IClassFixture<RuntimeFixture>
{
    /// <summary>A runaway plugin is stopped and the next call works.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ARunawayPluginIsStoppedAndTheNextCallWorks()
    {
        using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture(cpuBudgetMs: 200));
        CancellationToken cancel = TestContext.Current.CancellationToken;

        PluginCall<PluginNodePage> spinning = await plugin.SearchAsync(Text("spin"), cancel).ConfigureAwait(true);
        PluginCall<PluginNodePage> after = await plugin.SearchAsync(Text("series"), cancel).ConfigureAwait(true);

        Assert.True(spinning.Trapped);
        Assert.False(spinning.Outcome.IsSuccess);
        Assert.True(after.Outcome.IsSuccess);
        Assert.False(after.Trapped);
    }

    /// <summary>A panicking plugin is reported as trapped.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task APanickingPluginIsReportedAsTrapped()
    {
        using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture());

        PluginCall<PluginNodePage> call = await plugin.SearchAsync(Text("trap"), TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(call.Trapped);
        Assert.Equal(ErrorCode.Internal, call.Outcome.Error?.Code);
    }

    /// <summary>Logs come back with their levels.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LogsComeBackWithTheirLevels()
    {
        using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture());

        PluginCall<PluginNodePage> call = await plugin.SearchAsync(Text("log"), TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(
            [
                new PluginLogEntry(PluginLogLevel.Debug, "debug line"),
                new PluginLogEntry(PluginLogLevel.Info, "info line"),
                new PluginLogEntry(PluginLogLevel.Error, "error line"),
            ],
            call.Logs);
    }

    /// <summary>Settings reach the plugin and the clock can be frozen.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SettingsReachThePluginAndTheClockCanBeFrozen()
    {
        Dictionary<string, IReadOnlyList<string>> settings = new(StringComparer.Ordinal)
        {
            ["connection.user"] = ["alice"],
            ["search.maturity"] = ["safe", "suggestive"],
        };
        using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture(settings: settings, testing: new WasmTestingOptions(FixedNowMs: 1_700_000_000_123)));
        CancellationToken cancel = TestContext.Current.CancellationToken;

        PluginNode user = (await plugin.SearchAsync(Text("setting:connection.user"), cancel).ConfigureAwait(true)).Outcome.Value.Items.Single();
        PluginNode multi = (await plugin.SearchAsync(Text("setting:search.maturity"), cancel).ConfigureAwait(true)).Outcome.Value.Items.Single();
        PluginNode now = (await plugin.SearchAsync(Text("now"), cancel).ConfigureAwait(true)).Outcome.Value.Items.Single();

        Assert.Equal("alice", user.Title);
        Assert.Equal("safe", multi.Title);
        Assert.Equal(PropValue.FromText("safe|suggestive"), multi.Props.Single().Value);
        Assert.Equal("1700000000123", now.Title);
    }

    /// <summary>Network outside the allow list is refused naming the host.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task NetworkOutsideTheAllowListIsRefusedNamingTheHost()
    {
        using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture());

        PluginCall<PluginNodePage> call = await plugin.SearchAsync(Text("http:https://evil.example.net/x"), TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, call.Outcome.Error?.Code);
        Assert.Contains("evil.example.net", call.Outcome.Error!.Message, StringComparison.Ordinal);
    }

    /// <summary>Calls run concurrently and independently.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CallsRunConcurrentlyAndIndependently()
    {
        using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture());
        CancellationToken cancel = TestContext.Current.CancellationToken;

        PluginCall<PluginNodePage>[] calls = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => plugin.SearchAsync(Text("series"), cancel))).ConfigureAwait(true);

        Assert.All(calls, call => Assert.Equal(3, call.Outcome.Value.Items.Count));
    }

    /// <summary>A cancelled token stops the call before it starts.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ACancelledTokenStopsTheCallBeforeItStarts()
    {
        using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture());
        using CancellationTokenSource source = new();
        await source.CancelAsync().ConfigureAwait(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.SearchAsync(Text("series"), source.Token)).ConfigureAwait(true);
    }

    private static Query Text(string text) => new(text, [], [], null, null, null);
}
