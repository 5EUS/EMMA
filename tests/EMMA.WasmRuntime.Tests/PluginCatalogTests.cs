using EMMA.Domain;

using Xunit;

namespace EMMA.WasmRuntime.Tests;

/// <summary>Tests for plugin catalog through the native host.</summary>
/// <param name="runtime">The shared native runtime.</param>
public sealed class PluginCatalogTests(RuntimeFixture runtime) : IClassFixture<RuntimeFixture>
{
    /// <summary>Describe reports kinds filters and typed settings.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DescribeReportsKindsFiltersAndTypedSettings()
    {
        using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture());

        PluginCall<PluginInfo> call = await plugin.DescribeAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(call.Outcome.IsSuccess);
        PluginInfo info = call.Outcome.Value;
        KindInfo series = info.FindKind(NodeKind.Create("series"))!;
        Assert.Equal([NodeKind.Create("chapter")], series.Children);
        Assert.Equal(AssetRole.Create("image"), info.FindKind(NodeKind.Create("page"))!.Roles.Single());
        Assert.Null(series.MaxAge);
        Assert.Equal(TimeSpan.FromHours(1), info.FindKind(NodeKind.Create("recipe"))!.MaxAge);
        SettingSpec token = info.Settings.Single(setting => string.Equals(setting.Key, "connection.token", StringComparison.Ordinal));
        Assert.Equal(SettingType.Secret, token.Type);
        Assert.True(token.Required);
        SettingSpec timeout = info.Settings.Single(setting => string.Equals(setting.Key, "advanced.timeout", StringComparison.Ordinal));
        Assert.Equal(new SettingCondition("advanced.verbose", "true"), timeout.VisibleWhen);
        Assert.Equal(SettingType.Duration, timeout.Type);
        Assert.Equal("connection", info.Categories.Single().Id);
        FilterSpec status = info.Filters[0];
        Assert.Equal(FilterType.SingleChoice, status.Type);
        Assert.Equal("emma.filter.status", status.Role);
        FilterSpec tag = info.Filters.Single(filter => filter.Type == FilterType.Lookup);
        Assert.Equal("emma.filter.tag", tag.Role);
        Assert.Equal(new LookupSpec(2, 5, 250, false), tag.Lookup);
        Assert.Equal("recent", info.Sorts.Single().Value);
    }

    /// <summary>Health is reported.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task HealthIsReported()
    {
        using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture());

        PluginCall<PluginHealth> call = await plugin.HealthAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(new PluginHealth(true, null), call.Outcome.Value);
    }

    /// <summary>Search pages by cursor and carries typed props.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SearchPagesByCursorAndCarriesTypedProps()
    {
        using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture());

        PluginNodePage first = (await plugin.SearchAsync(Text("series", 2), TestContext.Current.CancellationToken).ConfigureAwait(true)).Outcome.Value;
        PluginNodePage second = (await plugin.SearchAsync(Text("series", 2, first.NextCursor), TestContext.Current.CancellationToken).ConfigureAwait(true)).Outcome.Value;

        Assert.Equal(["Series 1", "Series 2"], first.Items.Select(node => node.Title));
        Assert.Equal("2", first.NextCursor);
        Assert.Equal(3, first.Total);
        Assert.Equal(PropValue.FromNumber(7.5), first.Items[0].Props.Single(prop => string.Equals(prop.Key, "emma.rating", StringComparison.Ordinal)).Value);
        Assert.Equal(["Series 3"], second.Items.Select(node => node.Title));
        Assert.Null(second.NextCursor);
    }

    /// <summary>Children walk down and an unknown parent is not found.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ChildrenWalkDownAndAnUnknownParentIsNotFound()
    {
        using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture());

        PluginNodePage chapters = (await plugin.ChildrenAsync(LocalNodeId.Create("s1"), new PageRequest(null, 3), TestContext.Current.CancellationToken).ConfigureAwait(true)).Outcome.Value;
        PluginCall<PluginNodePage> missing = await plugin.ChildrenAsync(LocalNodeId.Create("nope"), new PageRequest(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(["Chapter 1", "Chapter 2", "Chapter 3"], chapters.Items.Select(node => node.Title));
        Assert.Equal(120, chapters.Total);
        Assert.Equal(ErrorCode.NotFound, missing.Outcome.Error?.Code);
        Assert.False(missing.Trapped);
    }

    /// <summary>Get returns known nodes in request order and omits unknown.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task GetReturnsKnownNodesInRequestOrderAndOmitsUnknown()
    {
        using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture());
        LocalNodeId[] ids = [LocalNodeId.Create("s2"), LocalNodeId.Create("unknown"), LocalNodeId.Create("r1")];

        PluginCall<IReadOnlyList<PluginNode>> call = await plugin.GetAsync(ids, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(["Series 2", "Lasagna"], call.Outcome.Value.Select(node => node.Title));
    }

    /// <summary>A plugin failure keeps its code message and retry delay.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task APluginFailureKeepsItsCodeMessageAndRetryDelay()
    {
        using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture());

        PluginCall<PluginNodePage> limited = await plugin.SearchAsync(Text("error:rate-limited"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        PluginCall<PluginNodePage> unauthorized = await plugin.SearchAsync(Text("error:unauthorized"), TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(new PluginError(ErrorCode.RateLimited, "slow down", TimeSpan.FromMilliseconds(1500)), limited.Outcome.Error);
        Assert.True(limited.Outcome.Error!.IsTransient);
        Assert.Equal(ErrorCode.Unauthorized, unauthorized.Outcome.Error?.Code);
    }

    private static Query Text(string text, int? limit = null, string? cursor = null) => new(text, [], [], null, cursor, limit);
}
