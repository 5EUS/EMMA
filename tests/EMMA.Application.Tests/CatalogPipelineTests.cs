using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.Infrastructure.InMemory;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.Application.Tests;

/// <summary>Tests for <see cref="CatalogPipeline"/>.</summary>
public sealed class CatalogPipelineTests
{
    private readonly FakeTimeProvider time = new();
    private readonly FakePluginGateway gateway = new();
    private readonly InMemoryNodeStore store = new();
    private readonly CatalogPipeline pipeline;

    /// <summary>Initializes a new instance of the <see cref="CatalogPipelineTests"/> class.</summary>
    public CatalogPipelineTests() =>
        this.pipeline = new CatalogPipeline(
            this.gateway,
            this.store,
            new PluginCallExecutor(
                new PluginCallOptions(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)),
                this.time));

    /// <summary>Search results are namespaced under the plugin and remembered in the store.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SearchNamespacesAndRemembersNodes()
    {
        this.gateway.OnSearch = (_, _) => Outcome.Success(TestData.RawPage("more", TestData.RawNode("s1"), TestData.RawNode("s2")));

        Outcome<NodePage> page = await this.pipeline.SearchAsync(TestData.Plugin, TestData.TextQuery("berserk"), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal([TestData.Ref("s1"), TestData.Ref("s2")], page.Value.Items.Select(node => node.Ref));
        Assert.Equal("more", page.Value.NextCursor);
        Assert.NotNull(await this.store.GetAsync(TestData.Ref("s1"), CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>A query that does not match the plugin's declaration never reaches the plugin.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task InvalidQueryNeverReachesThePlugin()
    {
        Query unknownFilter = new("x", [], [new FilterValue("nope", ["1"])]);

        Outcome<NodePage> page = await this.pipeline.SearchAsync(TestData.Plugin, unknownFilter, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, page.Error?.Code);
        Assert.Equal(0, this.gateway.SearchCalls);
    }

    /// <summary>A plugin failure comes back unchanged and nothing is stored.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task PluginFailurePassesThrough()
    {
        this.gateway.OnSearch = (_, _) => Outcome.Failure<PluginNodePage>(ErrorCode.Unauthorized, "log in");

        Outcome<NodePage> page = await this.pipeline.SearchAsync(TestData.Plugin, TestData.TextQuery("x"), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.Unauthorized, page.Error?.Code);
        Assert.Equal(1, this.gateway.SearchCalls);
    }

    /// <summary>A transient failure is retried and the eventual success is returned.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TransientFailureIsRetried()
    {
        this.gateway.OnSearch = (_, _) => this.gateway.SearchCalls < 3
            ? Outcome.Failure<PluginNodePage>(ErrorCode.Unavailable, "down")
            : Outcome.Success(TestData.RawPage(null, TestData.RawNode("s1")));

        Outcome<NodePage> page = await TimeRunner.RunAsync(
            this.pipeline.SearchAsync(TestData.Plugin, TestData.TextQuery("x"), CancellationToken.None),
            this.time).ConfigureAwait(true);

        Assert.True(page.IsSuccess);
        Assert.Equal(3, this.gateway.SearchCalls);
    }

    /// <summary>A response that breaks the contract is rejected before it reaches the store.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ContractViolationNeverReachesTheStore()
    {
        this.gateway.OnSearch = (_, _) => Outcome.Success(TestData.RawPage(null, TestData.RawNode("ok"), TestData.RawNode("bad", "podcast")));

        Outcome<NodePage> page = await this.pipeline.SearchAsync(TestData.Plugin, TestData.TextQuery("x"), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.Internal, page.Error?.Code);
        Assert.Null(await this.store.GetAsync(TestData.Ref("ok"), CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>A describe failure fails the call without searching.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DescribeFailureFailsTheCall()
    {
        this.gateway.OnDescribe = _ => Outcome.Failure<PluginInfo>(ErrorCode.NotFound, "no such plugin");

        Outcome<NodePage> page = await this.pipeline.SearchAsync(TestData.Plugin, TestData.TextQuery("x"), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.NotFound, page.Error?.Code);
        Assert.Equal(0, this.gateway.SearchCalls);
    }

    /// <summary>Children inherit the requested parent when the plugin does not say, and the plugin sees the local id.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ChildrenGetTheRequestedParent()
    {
        LocalNodeId? seenParent = null;
        this.gateway.OnChildren = (_, parent, _) =>
        {
            seenParent = parent;
            return Outcome.Success(TestData.RawPage(null, TestData.RawNode("c1", "chapter")));
        };

        Outcome<NodePage> page = await this.pipeline.ChildrenAsync(TestData.Ref("s1"), new PageRequest(), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(LocalNodeId.Create("s1"), seenParent);
        Assert.Equal(TestData.Ref("s1"), page.Value.Items[0].Parent);
    }

    /// <summary>The browse root asks the plugin for children of nothing.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task BrowseRootUsesNoParent()
    {
        bool sawNull = false;
        this.gateway.OnChildren = (_, parent, _) =>
        {
            sawNull = parent is null;
            return Outcome.Success(TestData.RawPage(null));
        };

        await this.pipeline.BrowseRootAsync(TestData.Plugin, new PageRequest(), CancellationToken.None).ConfigureAwait(true);

        Assert.True(sawNull);
    }

    /// <summary>A bad limit is rejected without calling the plugin.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task BadPagingIsRejected()
    {
        Outcome<NodePage> page = await this.pipeline.ChildrenAsync(TestData.Ref("s1"), new PageRequest(null, 0), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, page.Error?.Code);
        Assert.Equal(0, this.gateway.ChildrenCalls);
    }

    /// <summary>Get returns nodes in the order requested across plugins and omits unknown ones.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task GetPreservesOrderAcrossPluginsAndOmitsUnknown()
    {
        this.gateway.OnGet = (_, ids) => Outcome.Success<IReadOnlyList<PluginNode>>(
            [.. ids.Where(id => !string.Equals(id.Value, "missing", StringComparison.Ordinal)).Reverse().Select(id => TestData.RawNode(id.Value))]);
        NodeRef other = new(TestData.OtherPlugin, LocalNodeId.Create("o1"));
        NodeRef[] requested = [TestData.Ref("a"), other, TestData.Ref("missing"), TestData.Ref("b")];

        Outcome<IReadOnlyList<Node>> nodes = await this.pipeline.GetAsync(requested, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal([TestData.Ref("a"), other, TestData.Ref("b")], nodes.Value.Select(node => node.Ref));
        Assert.Equal(2, this.gateway.GetCalls);
    }

    /// <summary>A plugin that returns a node nobody asked for is rejected.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task GetRejectsUnrequestedNodes()
    {
        this.gateway.OnGet = (_, _) => Outcome.Success<IReadOnlyList<PluginNode>>([TestData.RawNode("a"), TestData.RawNode("surprise")]);

        Outcome<IReadOnlyList<Node>> nodes = await this.pipeline.GetAsync([TestData.Ref("a")], CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.Internal, nodes.Error?.Code);
        Assert.Contains("surprise", nodes.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>Asking for more nodes than the limit is rejected.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task GetRejectsTooManyIds()
    {
        NodeRef[] requested = [.. Enumerable.Range(0, ContractLimits.MaxIdsPerGet + 1).Select(i => TestData.Ref($"n{i}"))];

        Outcome<IReadOnlyList<Node>> nodes = await this.pipeline.GetAsync(requested, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, nodes.Error?.Code);
        Assert.Equal(0, this.gateway.GetCalls);
    }
}
