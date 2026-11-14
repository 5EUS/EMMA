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
    private readonly InMemoryNodeListingStore listings = new();
    private readonly CatalogPipeline pipeline;

    /// <summary>Initializes a new instance of the <see cref="CatalogPipelineTests"/> class.</summary>
    public CatalogPipelineTests() =>
        this.pipeline = new CatalogPipeline(
            this.gateway,
            this.store,
            this.listings,
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

    /// <summary>Describing returns a valid declaration and rejects one that breaks the contract.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DescribeReturnsOnlyValidDeclarations()
    {
        Outcome<PluginInfo> valid = await this.pipeline.DescribeAsync(TestData.Plugin, CancellationToken.None).ConfigureAwait(true);
        this.gateway.OnDescribe = _ => Outcome.Success(TestData.Info() with { Kinds = [TestData.Kind("series", ["ghost"])] });
        Outcome<PluginInfo> invalid = await this.pipeline.DescribeAsync(TestData.Plugin, CancellationToken.None).ConfigureAwait(true);

        Assert.True(valid.IsSuccess);
        Assert.Equal(ErrorCode.Internal, invalid.Error?.Code);
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

    /// <summary>Suggestions are limited to what the filter allows and ignore text that is too short.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SuggestAsksOnlyForLookupFiltersWithEnoughText()
    {
        this.gateway.OnDescribe = _ => Outcome.Success(TestData.Info([LookupFilter(), TestData.Filter("status", FilterType.SingleChoice, false, "done")]));
        int asked = 0;
        this.gateway.OnSuggest = (_, _, text, limit) =>
        {
            asked = limit;
            return Outcome.Success<IReadOnlyList<Choice>>([.. Enumerable.Range(1, 9).Select(i => new Choice($"t{i}", $"{text} {i}", null, false))]);
        };

        Outcome<IReadOnlyList<Choice>> good = await this.pipeline.SuggestAsync(TestData.Plugin, "tag", "act", 20, CancellationToken.None).ConfigureAwait(true);
        Outcome<IReadOnlyList<Choice>> tooShort = await this.pipeline.SuggestAsync(TestData.Plugin, "tag", "a", 5, CancellationToken.None).ConfigureAwait(true);
        Outcome<IReadOnlyList<Choice>> notLookup = await this.pipeline.SuggestAsync(TestData.Plugin, "status", "done", 5, CancellationToken.None).ConfigureAwait(true);
        Outcome<IReadOnlyList<Choice>> unknown = await this.pipeline.SuggestAsync(TestData.Plugin, "nope", "act", 5, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(3, asked);
        Assert.Equal(3, good.Value.Count);
        Assert.Empty(tooShort.Value);
        Assert.Equal(ErrorCode.InvalidArgument, notLookup.Error?.Code);
        Assert.Equal(ErrorCode.InvalidArgument, unknown.Error?.Code);
        Assert.Equal(1, this.gateway.SuggestCalls);
    }

    /// <summary>A suggestion with a blank value is a contract violation.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SuggestRejectsBlankValues()
    {
        this.gateway.OnDescribe = _ => Outcome.Success(TestData.Info([LookupFilter()]));
        this.gateway.OnSuggest = (_, _, _, _) => Outcome.Success<IReadOnlyList<Choice>>([new Choice(" ", "Label", null, false)]);

        Outcome<IReadOnlyList<Choice>> result = await this.pipeline.SuggestAsync(TestData.Plugin, "tag", "act", 5, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.Internal, result.Error?.Code);
    }

    /// <summary>A plugin without search assist answers unsupported.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SuggestPassesUnsupportedThrough()
    {
        this.gateway.OnDescribe = _ => Outcome.Success(TestData.Info([LookupFilter()]));

        Outcome<IReadOnlyList<Choice>> result = await this.pipeline.SuggestAsync(TestData.Plugin, "tag", "act", 5, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.Unsupported, result.Error?.Code);
    }

    /// <summary>Enrichment replaces props with the same key, keeps the rest, and is remembered.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task EnrichMergesPropsOverTheStoredNode()
    {
        this.gateway.OnSearch = (_, _) => Outcome.Success(TestData.RawPage(null, TestData.RawNode("s1", title: "Berserk") with
        {
            Props = [new Prop("emma.synopsis", PropValue.FromText("Dark")), new Prop("emma.rating", PropValue.FromNumber(1))],
        }));
        this.gateway.OnEnrich = (_, ids) => Outcome.Success<IReadOnlyList<PluginNode>>([.. ids.Select(id => TestData.RawNode(id.Value, title: "Ignored") with
        {
            Props = [new Prop("emma.rating", PropValue.FromNumber(9))],
        })]);
        await this.pipeline.SearchAsync(TestData.Plugin, TestData.TextQuery("berserk"), CancellationToken.None).ConfigureAwait(true);

        Outcome<IReadOnlyList<Node>> result = await this.pipeline.EnrichAsync([TestData.Ref("s1"), TestData.Ref("unseen")], CancellationToken.None).ConfigureAwait(true);

        Node node = Assert.Single(result.Value);
        Assert.Equal("Berserk", node.Title);
        Assert.Equal(["emma.synopsis", "emma.rating"], node.Props.Select(prop => prop.Key));
        Assert.Equal(PropValue.FromNumber(9), node.Props.Single(prop => string.Equals(prop.Key, "emma.rating", StringComparison.Ordinal)).Value);
        Node? stored = await this.store.GetAsync(TestData.Ref("s1"), CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(PropValue.FromNumber(9), stored?.Props.Single(prop => string.Equals(prop.Key, "emma.rating", StringComparison.Ordinal)).Value);
    }

    /// <summary>Nodes the host has not seen never reach the plugin.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task EnrichSkipsTheCallWhenNothingWasSeen()
    {
        Outcome<IReadOnlyList<Node>> result = await this.pipeline.EnrichAsync([TestData.Ref("unseen")], CancellationToken.None).ConfigureAwait(true);

        Assert.Empty(result.Value);
        Assert.Equal(0, this.gateway.EnrichCalls);
    }

    /// <summary>A node fetched in full is answered from the store the next time, with no plugin call.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task GetServesStoredDetailsWithoutAskingThePlugin()
    {
        this.gateway.OnGet = (_, ids) => Outcome.Success<IReadOnlyList<PluginNode>>([.. ids.Select(id => TestData.RawNode(id.Value))]);

        await this.pipeline.GetAsync([TestData.Ref("a")], CancellationToken.None).ConfigureAwait(true);
        Outcome<IReadOnlyList<Node>> again = await this.pipeline.GetAsync([TestData.Ref("a")], CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(1, this.gateway.GetCalls);
        Assert.Equal(TestData.Ref("a"), Assert.Single(again.Value).Ref);
    }

    /// <summary>A node only seen in a search is not trusted as full details, so the plugin is asked.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task GetDoesNotTrustNodesSeenOnlyInSearch()
    {
        this.gateway.OnSearch = (_, _) => Outcome.Success(TestData.RawPage(null, TestData.RawNode("s1")));
        this.gateway.OnGet = (_, ids) => Outcome.Success<IReadOnlyList<PluginNode>>([.. ids.Select(id => TestData.RawNode(id.Value))]);
        await this.pipeline.SearchAsync(TestData.Plugin, TestData.TextQuery("x"), CancellationToken.None).ConfigureAwait(true);

        await this.pipeline.GetAsync([TestData.Ref("s1")], CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(1, this.gateway.GetCalls);
    }

    /// <summary>With several references, only the ones the host does not hold in full go to the plugin, and the order asked is kept.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task GetAsksOnlyForWhatIsMissingAndKeepsOrder()
    {
        List<string> asked = [];
        this.gateway.OnGet = (_, ids) =>
        {
            asked.AddRange(ids.Select(id => id.Value));
            return Outcome.Success<IReadOnlyList<PluginNode>>([.. ids.Select(id => TestData.RawNode(id.Value))]);
        };
        await this.pipeline.GetAsync([TestData.Ref("a")], CancellationToken.None).ConfigureAwait(true);
        asked.Clear();

        Outcome<IReadOnlyList<Node>> nodes = await this.pipeline.GetAsync([TestData.Ref("b"), TestData.Ref("a")], CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(["b"], asked);
        Assert.Equal(["b", "a"], nodes.Value.Select(node => node.Ref.Id.Value));
    }

    /// <summary>Asking with <see cref="CacheMode.Refresh"/> always calls the plugin and replaces the stored node.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RefreshModeBypassesAndOverwritesTheStore()
    {
        string title = "Old";
        this.gateway.OnGet = (_, ids) => Outcome.Success<IReadOnlyList<PluginNode>>([.. ids.Select(id => TestData.RawNode(id.Value, title: title))]);
        await this.pipeline.GetAsync([TestData.Ref("a")], CancellationToken.None).ConfigureAwait(true);

        title = "New";
        Outcome<IReadOnlyList<Node>> refreshed = await this.pipeline.GetAsync([TestData.Ref("a")], CacheMode.Refresh, CancellationToken.None).ConfigureAwait(true);
        Outcome<IReadOnlyList<Node>> after = await this.pipeline.GetAsync([TestData.Ref("a")], CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(2, this.gateway.GetCalls);
        Assert.Equal("New", refreshed.Value[0].Title);
        Assert.Equal("New", after.Value[0].Title);
    }

    /// <summary>A node's children are listed completely once, then every page is served from the stored listing with the host's own cursors.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ChildrenAreListedOnceThenServedFromTheStore()
    {
        await this.store.UpsertAsync([Node("s", "series")], CancellationToken.None).ConfigureAwait(true);
        this.gateway.OnChildren = (_, parent, request) => request.Cursor is null
            ? Outcome.Success(TestData.RawPage("p2", TestData.RawNode("c1", "chapter", parent: parent?.Value), TestData.RawNode("c2", "chapter", parent: parent?.Value)))
            : Outcome.Success(TestData.RawPage(null, TestData.RawNode("c3", "chapter", parent: parent?.Value)));

        Outcome<NodePage> first = await this.pipeline.ChildrenAsync(TestData.Ref("s"), new PageRequest(null, 2), CancellationToken.None).ConfigureAwait(true);
        int callsAfterFirst = this.gateway.ChildrenCalls;
        Outcome<NodePage> second = await this.pipeline.ChildrenAsync(TestData.Ref("s"), new PageRequest(first.Value.NextCursor, 2), CancellationToken.None).ConfigureAwait(true);
        Outcome<NodePage> again = await this.pipeline.ChildrenAsync(TestData.Ref("s"), new PageRequest(null, 2), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(["c1", "c2"], first.Value.Items.Select(node => node.Ref.Id.Value));
        Assert.Equal("emma:2", first.Value.NextCursor);
        Assert.Equal(3, first.Value.Total);
        Assert.Equal(["c3"], second.Value.Items.Select(node => node.Ref.Id.Value));
        Assert.Null(second.Value.NextCursor);
        Assert.Equal(["c1", "c2"], again.Value.Items.Select(node => node.Ref.Id.Value));
        Assert.Equal(2, callsAfterFirst);
        Assert.Equal(callsAfterFirst, this.gateway.ChildrenCalls);
    }

    /// <summary>A refresh lists the children again and replaces the stored listing, including removing a child the plugin dropped.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RefreshModeRelistsTheChildren()
    {
        await this.store.UpsertAsync([Node("s", "series")], CancellationToken.None).ConfigureAwait(true);
        List<string> chapters = ["c1", "c2"];
        this.gateway.OnChildren = (_, parent, _) => Outcome.Success(TestData.RawPage(null, [.. chapters.Select(id => TestData.RawNode(id, "chapter", parent: parent?.Value))]));
        await this.pipeline.ChildrenAsync(TestData.Ref("s"), new PageRequest(), CancellationToken.None).ConfigureAwait(true);

        chapters.Remove("c1");
        chapters.Add("c3");
        Outcome<NodePage> refreshed = await this.pipeline.ChildrenAsync(TestData.Ref("s"), new PageRequest(), CacheMode.Refresh, CancellationToken.None).ConfigureAwait(true);
        Outcome<NodePage> after = await this.pipeline.ChildrenAsync(TestData.Ref("s"), new PageRequest(), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(["c2", "c3"], refreshed.Value.Items.Select(node => node.Ref.Id.Value));
        Assert.Equal(["c2", "c3"], after.Value.Items.Select(node => node.Ref.Id.Value));
        Assert.Equal(2, this.gateway.ChildrenCalls);
        Assert.NotNull(await this.store.GetAsync(TestData.Ref("c1"), CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>A failure part-way through listing stores no listing, so the next read asks the plugin again.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AFailedListingIsNotStored()
    {
        await this.store.UpsertAsync([Node("s", "series")], CancellationToken.None).ConfigureAwait(true);
        bool fail = true;
        this.gateway.OnChildren = (_, parent, request) =>
        {
            if (request.Cursor is null)
            {
                return Outcome.Success(TestData.RawPage("p2", TestData.RawNode("c1", "chapter", parent: parent?.Value)));
            }

            return fail
                ? Outcome.Failure<PluginNodePage>(ErrorCode.InvalidArgument, "boom")
                : Outcome.Success(TestData.RawPage(null, TestData.RawNode("c2", "chapter", parent: parent?.Value)));
        };

        Outcome<NodePage> failed = await this.pipeline.ChildrenAsync(TestData.Ref("s"), new PageRequest(), CancellationToken.None).ConfigureAwait(true);
        fail = false;
        Outcome<NodePage> retried = await this.pipeline.ChildrenAsync(TestData.Ref("s"), new PageRequest(), CancellationToken.None).ConfigureAwait(true);

        Assert.False(failed.IsSuccess);
        Assert.Equal(["c1", "c2"], retried.Value.Items.Select(node => node.Ref.Id.Value));
    }

    /// <summary>A node of a kind with no children, or one the host has not seen, is asked live and keeps the plugin's cursors.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LeafKindsAndUnseenParentsAreAskedLive()
    {
        await this.store.UpsertAsync([Node("p", "page")], CancellationToken.None).ConfigureAwait(true);
        this.gateway.OnChildren = (_, parent, _) => Outcome.Success(TestData.RawPage("plugin-cursor", TestData.RawNode("x", "page", parent: parent?.Value)));

        Outcome<NodePage> leaf = await this.pipeline.ChildrenAsync(TestData.Ref("p"), new PageRequest(), CancellationToken.None).ConfigureAwait(true);
        Outcome<NodePage> unseen = await this.pipeline.ChildrenAsync(TestData.Ref("never"), new PageRequest(), CancellationToken.None).ConfigureAwait(true);
        Outcome<NodePage> foreign = await this.pipeline.ChildrenAsync(TestData.Ref("p"), new PageRequest("plugin-cursor"), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal("plugin-cursor", leaf.Value.NextCursor);
        Assert.Equal("plugin-cursor", unseen.Value.NextCursor);
        Assert.Equal("plugin-cursor", foreign.Value.NextCursor);
        Assert.Equal(3, this.gateway.ChildrenCalls);
    }

    private static FilterSpec LookupFilter() => TestData.Filter("tag", FilterType.Lookup) with { Lookup = new LookupSpec(2, 3, 100, false) };

    private static Node Node(string id, string kind) =>
        new(TestData.Ref(id), NodeKind.Create(kind), id, null, null, []);
}
