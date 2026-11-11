using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Runtime.Tests;

/// <summary>Tests refreshing saved nodes and reloading plugins, over the real stores and a scripted plugin.</summary>
public sealed class RefreshTests
{
    private static readonly UserId User = TestData.User;
    private static readonly NodeKind Series = NodeKind.Create("series");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A refresh reports the children that appeared since the host last listed them, once.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task NewChildrenAreReportedOnce()
    {
        FakePluginGateway gateway = new();
        List<string> chapters = ["c1", "c2"];
        gateway.OnChildren = (_, parent, _) => Outcome.Success(Chapters(parent, chapters));
        return WithRuntimeAsync(gateway, ["a"], async runtime =>
        {
            await runtime.Catalog.ChildrenAsync(TestData.Ref("a"), new PageRequest(), Ct).ConfigureAwait(true);

            chapters.Add("c3");
            Outcome<RefreshReport> first = await runtime.Refresh.RefreshLibraryAsync(User, null, Ct).ConfigureAwait(true);
            Outcome<RefreshReport> second = await runtime.Refresh.RefreshLibraryAsync(User, null, Ct).ConfigureAwait(true);

            NodeUpdate update = Assert.Single(first.Value.Updated);
            Assert.Equal(1, first.Value.Checked);
            Assert.Equal(TestData.Ref("a"), update.Node.Ref);
            Assert.Equal(["c3"], update.NewChildren.Select(child => child.Ref.Id.Value));
            Assert.Empty(first.Value.Failed);
            Assert.Empty(second.Value.Updated);
        });
    }

    /// <summary>A node whose children were never listed has nothing to compare with: its children are stored, and only later arrivals are reported.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task FirstRefreshOnlyEstablishesTheBaseline()
    {
        FakePluginGateway gateway = new();
        List<string> chapters = ["c1", "c2"];
        gateway.OnChildren = (_, parent, _) => Outcome.Success(Chapters(parent, chapters));
        return WithRuntimeAsync(gateway, ["a"], async runtime =>
        {
            Outcome<RefreshReport> first = await runtime.Refresh.RefreshLibraryAsync(User, null, Ct).ConfigureAwait(true);
            chapters.Add("c3");
            Outcome<RefreshReport> second = await runtime.Refresh.RefreshLibraryAsync(User, null, Ct).ConfigureAwait(true);

            Assert.Equal(1, first.Value.Checked);
            Assert.Empty(first.Value.Updated);
            Assert.Equal(["c3"], Assert.Single(second.Value.Updated).NewChildren.Select(child => child.Ref.Id.Value));
        });
    }

    /// <summary>Every page of children is read, and a library can be refreshed on its own.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task EveryPageIsReadAndOneLibraryCanBeRefreshed()
    {
        FakePluginGateway gateway = new();
        gateway.OnChildren = (_, parent, request) => request.Cursor is null
            ? Outcome.Success(TestData.RawPage("next", TestData.RawNode("c1", "chapter", parent: parent?.Value)))
            : Outcome.Success(TestData.RawPage(null, TestData.RawNode("c2", "chapter", parent: parent?.Value)));
        return WithRuntimeAsync(gateway, ["a", "b"], async runtime =>
        {
            await runtime.Library.CreateAsync(User, "Work", Ct).ConfigureAwait(true);
            await runtime.Library.MoveAsync(User, TestData.Ref("b"), "Work", Ct).ConfigureAwait(true);

            Outcome<RefreshReport> work = await runtime.Refresh.RefreshLibraryAsync(User, "work", Ct).ConfigureAwait(true);
            Outcome<RefreshReport> missing = await runtime.Refresh.RefreshLibraryAsync(User, "Nowhere", Ct).ConfigureAwait(true);

            Assert.Equal(1, work.Value.Checked);
            Assert.Equal(2, gateway.ChildrenCalls);
            Assert.Equal(2, (await runtime.Nodes.ChildIdsAsync(TestData.Ref("b"), Ct).ConfigureAwait(true)).Count);
            Assert.Equal(ErrorCode.NotFound, missing.Error?.Code);
        });
    }

    /// <summary>One node failing is reported and the others are still refreshed; a plugin that cannot be described fails all its nodes.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task FailuresAreReportedPerNode()
    {
        FakePluginGateway gateway = new();
        List<string> chapters = ["c1"];
        gateway.OnChildren = (_, parent, _) => string.Equals(parent?.Value, "bad", StringComparison.Ordinal)
            ? Outcome.Failure<PluginNodePage>(ErrorCode.Unavailable, "down")
            : Outcome.Success(Chapters(parent, chapters));
        return WithRuntimeAsync(gateway, ["bad", "good"], async runtime =>
        {
            await runtime.Catalog.ChildrenAsync(TestData.Ref("good"), new PageRequest(), Ct).ConfigureAwait(true);
            chapters.Add("c2");

            Outcome<RefreshReport> partial = await runtime.Refresh.RefreshLibraryAsync(User, null, Ct).ConfigureAwait(true);
            gateway.OnDescribe = _ => Outcome.Failure<PluginInfo>(ErrorCode.Unavailable, "gone");
            Outcome<RefreshReport> allFailed = await runtime.Refresh.RefreshLibraryAsync(User, null, Ct).ConfigureAwait(true);

            Assert.Equal(2, partial.Value.Checked);
            Assert.Equal(TestData.Ref("bad"), Assert.Single(partial.Value.Failed).Node);
            Assert.Equal(ErrorCode.Unavailable, partial.Value.Failed[0].Error.Code);
            Assert.Equal(TestData.Ref("good"), Assert.Single(partial.Value.Updated).Node.Ref);
            Assert.Equal(2, allFailed.Value.Failed.Count);
            Assert.Empty(allFailed.Value.Updated);
        });
    }

    /// <summary>A saved node whose kind has no children is a leaf: it counts as checked and the plugin is not asked to list it.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task LeavesAreNotListed()
    {
        FakePluginGateway gateway = new();
        return WithRuntimeAsync(gateway, [], async runtime =>
        {
            await runtime.Nodes.UpsertAsync([new Node(TestData.Ref("leaf"), NodeKind.Create("page"), "Leaf", null, null, [])], Ct).ConfigureAwait(true);
            await runtime.Library.AddAsync(User, TestData.Ref("leaf"), null, Ct).ConfigureAwait(true);

            Outcome<RefreshReport> report = await runtime.Refresh.RefreshLibraryAsync(User, null, Ct).ConfigureAwait(true);

            Assert.Equal(1, report.Value.Checked);
            Assert.Empty(report.Value.Updated);
            Assert.Empty(report.Value.Failed);
            Assert.Equal(0, gateway.ChildrenCalls);
        });
    }

    /// <summary>Refreshing a node asks the plugin again and stores the new thumbnail; an unseen node, or one the plugin dropped, is not found.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task RefreshingANodeStoresItsNewThumbnail()
    {
        FakePluginGateway gateway = new();
        string thumbnail = "https://example.com/old.jpg";
        gateway.OnGet = (_, ids) => Outcome.Success<IReadOnlyList<PluginNode>>(
            [.. ids.Where(id => string.Equals(id.Value, "a", StringComparison.Ordinal)).Select(id => new PluginNode(id.Value, "series", "Title A", null, thumbnail, []))]);
        return WithRuntimeAsync(gateway, ["a", "b"], async runtime =>
        {
            thumbnail = "https://example.com/new.jpg";
            Outcome<Node> refreshed = await runtime.Refresh.RefreshNodeAsync(TestData.Ref("a"), Ct).ConfigureAwait(true);
            Outcome<Node> dropped = await runtime.Refresh.RefreshNodeAsync(TestData.Ref("b"), Ct).ConfigureAwait(true);
            Outcome<Node> unseen = await runtime.Refresh.RefreshNodeAsync(TestData.Ref("never"), Ct).ConfigureAwait(true);

            Node stored = (await runtime.Nodes.GetAsync(TestData.Ref("a"), Ct).ConfigureAwait(true))!;
            Assert.Equal("https://example.com/new.jpg", refreshed.Value.Thumbnail?.AbsoluteUri);
            Assert.Equal("https://example.com/new.jpg", stored.Thumbnail?.AbsoluteUri);
            Assert.Equal(ErrorCode.NotFound, dropped.Error?.Code);
            Assert.Equal(ErrorCode.NotFound, unseen.Error?.Code);
        });
    }

    /// <summary>A plugin changed by another process is picked up by a reload, and not before.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ReloadPicksUpChangesMadeElsewhere()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            PluginId fixture = PluginId.Create(RuntimeHarness.Plugin);
            await host.Runtime.InitializeAsync(Ct).ConfigureAwait(true);
            await host.InstallAsync().ConfigureAwait(true);
            await host.Runtime.Catalog.SearchAsync(fixture, TestData.TextQuery("series"), Ct).ConfigureAwait(true);

            // The command line, in another process, switches the plugin off.
            EmmaRuntime other = host.Open();
            await using (other.ConfigureAwait(true))
            {
                await other.InitializeAsync(Ct).ConfigureAwait(true);
                await other.SetStatusAsync(fixture, PluginStatus.Disabled, Ct).ConfigureAwait(true);
            }

            Outcome<NodePage> stale = await host.Runtime.Catalog.SearchAsync(fixture, TestData.TextQuery("series"), Ct).ConfigureAwait(true);
            IReadOnlyList<PluginId> quarantined = await host.Runtime.ReloadPluginsAsync(Ct).ConfigureAwait(true);
            Outcome<NodePage> reloaded = await host.Runtime.Catalog.SearchAsync(fixture, TestData.TextQuery("series"), Ct).ConfigureAwait(true);

            Assert.True(stale.IsSuccess, "an open plugin keeps answering until the runtime reloads");
            Assert.Empty(quarantined);
            Assert.Equal(ErrorCode.Unsupported, reloaded.Error?.Code);
        }
    }

    private static PluginNodePage Chapters(LocalNodeId? parent, List<string> ids) =>
        TestData.RawPage(null, [.. ids.Select(id => TestData.RawNode(id, "chapter", parent: parent?.Value))]);

    // Opens a runtime over the scripted plugin with a node saved for each id, runs the test body, and cleans up.
    private static async Task WithRuntimeAsync(FakePluginGateway gateway, string[] saved, Func<EmmaRuntime, Task> body)
    {
        RuntimeHarness harness = await RuntimeHarness.StartAsync().ConfigureAwait(false);
        await using (harness.ConfigureAwait(false))
        {
            EmmaRuntime runtime = harness.Open(options => options with
            {
                GatewayOverride = gateway,
                CallOptions = new PluginCallOptions(1, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(30)),
            });
            await using (runtime.ConfigureAwait(false))
            {
                await runtime.InitializeAsync(Ct).ConfigureAwait(false);
                await runtime.Nodes.UpsertAsync([.. saved.Select(id => new Node(TestData.Ref(id), Series, $"Title {id}", null, null, []))], Ct).ConfigureAwait(false);
                foreach (string id in saved)
                {
                    await runtime.Library.AddAsync(User, TestData.Ref(id), null, Ct).ConfigureAwait(false);
                }

                await body(runtime).ConfigureAwait(false);
            }
        }
    }
}
