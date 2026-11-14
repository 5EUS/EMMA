using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Storage.Tests;

/// <summary>Tests for <see cref="SqliteNodeStore"/>.</summary>
public sealed class SqliteNodeStoreTests : StorageTestBase
{
    private SqliteNodeStore Store => new(this.Options, this.Time);

    /// <summary>A node with every prop kind comes back exactly as stored.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RoundTripsEveryPropKind()
    {
        Node node = MakeNode("a", "root") with
        {
            Thumbnail = TestData.Url("img.example.com", "/t.png"),
            Props =
            [
                new Prop("emma.synopsis", PropValue.FromText("Text with \"quotes\", unicode é and 日本語")),
                new Prop("acme.count", PropValue.FromWholeNumber(long.MaxValue)),
                new Prop("emma.rating", PropValue.FromNumber(8.5)),
                new Prop("acme.adult", PropValue.FromFlag(true)),
                new Prop("emma.released-ms", PropValue.FromTimestamp(1_700_000_000_000)),
                new Prop("acme.site", PropValue.FromUrl(TestData.Url("example.com", "/s"))),
                new Prop("emma.tags", PropValue.FromTextList(["a", "b", string.Empty])),
            ],
        };

        await this.Store.UpsertAsync([node], Ct);
        Node? loaded = await this.Store.GetAsync(node.Ref, Ct);

        Assert.NotNull(loaded);
        Assert.Equal(node.Ref, loaded.Ref);
        Assert.Equal(node.Kind, loaded.Kind);
        Assert.Equal(node.Title, loaded.Title);
        Assert.Equal(node.Parent, loaded.Parent);
        Assert.Equal(node.Thumbnail, loaded.Thumbnail);
        Assert.Equal(node.Props, loaded.Props);
    }

    /// <summary>Non-finite numbers survive storage instead of failing the whole batch.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task NonFiniteNumbersSurvive()
    {
        Node node = MakeNode("a") with
        {
            Props = [new Prop("acme.nan", PropValue.FromNumber(double.NaN)), new Prop("acme.inf", PropValue.FromNumber(double.PositiveInfinity))],
        };

        await this.Store.UpsertAsync([node], Ct);
        Node? loaded = await this.Store.GetAsync(node.Ref, Ct);

        Assert.True(double.IsNaN(loaded!.Props[0].Value.Number));
        Assert.True(double.IsPositiveInfinity(loaded.Props[1].Value.Number));
    }

    /// <summary>Props keep the order the plugin gave them.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task PropOrderIsPreserved()
    {
        Node node = MakeNode("a") with
        {
            Props = [.. Enumerable.Range(0, 20).Select(i => new Prop($"acme.p{i}", PropValue.FromWholeNumber(i)))],
        };

        await this.Store.UpsertAsync([node], Ct);

        Assert.Equal(node.Props.Select(prop => prop.Key), (await this.Store.GetAsync(node.Ref, Ct))!.Props.Select(prop => prop.Key));
    }

    /// <summary>An unknown node is simply not found.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UnknownNodeIsNull() => Assert.Null(await this.Store.GetAsync(TestData.Ref("nope"), Ct));

    /// <summary>Storing a node again replaces every field.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UpsertReplaces()
    {
        await this.Store.UpsertAsync([MakeNode("a", "p1") with { Props = [new Prop("acme.x", PropValue.FromFlag(true))] }], Ct);
        this.Time.Advance(TimeSpan.FromMinutes(5));

        await this.Store.UpsertAsync([MakeNode("a") with { Title = "Renamed" }], Ct);
        Node? loaded = await this.Store.GetAsync(TestData.Ref("a"), Ct);

        Assert.Equal("Renamed", loaded!.Title);
        Assert.Null(loaded.Parent);
        Assert.Empty(loaded.Props);
        Assert.Equal(this.Time.GetUtcNow().ToUnixTimeMilliseconds(), await this.ScalarAsync("SELECT updated_at FROM nodes WHERE local_id = 'a'"));
        Assert.Equal(1L, await this.ScalarAsync("SELECT COUNT(*) FROM nodes"));
    }

    /// <summary>The same local id under two plugins is two different nodes.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SameLocalIdUnderTwoPluginsDoesNotCollide()
    {
        NodeRef first = new(TestData.Plugin, LocalNodeId.Create("1"));
        NodeRef second = new(TestData.OtherPlugin, LocalNodeId.Create("1"));

        await this.Store.UpsertAsync([MakeNode("1") with { Ref = first, Title = "first" }, MakeNode("1") with { Ref = second, Title = "second" }], Ct);

        Assert.Equal("first", (await this.Store.GetAsync(first, Ct))!.Title);
        Assert.Equal("second", (await this.Store.GetAsync(second, Ct))!.Title);
    }

    /// <summary>Hostile text is stored as data and never interpreted as SQL.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task HostileTextIsJustData()
    {
        const string Hostile = "x'); DROP TABLE nodes; --";
        Node node = new(
            new NodeRef(TestData.Plugin, LocalNodeId.Create(Hostile)),
            NodeKind.Create("series"),
            Hostile,
            null,
            null,
            [new Prop("acme.note", PropValue.FromText(Hostile))]);

        await this.Store.UpsertAsync([node], Ct);
        Node? loaded = await this.Store.GetAsync(node.Ref, Ct);

        Assert.Equal(Hostile, loaded!.Title);
        Assert.Equal(Hostile, loaded.Props[0].Value.Text);
        Assert.Equal(1L, await this.ScalarAsync("SELECT COUNT(*) FROM nodes"));
    }

    /// <summary>A parent in another plugin is refused, and nothing from the batch is written.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ParentInAnotherPluginIsRefusedAndNothingIsWritten()
    {
        Node good = MakeNode("good");
        Node bad = MakeNode("bad") with { Parent = new NodeRef(TestData.OtherPlugin, LocalNodeId.Create("p")) };

        await Assert.ThrowsAsync<ArgumentException>(() => this.Store.UpsertAsync([good, bad], Ct));

        Assert.Equal(0L, await this.ScalarAsync("SELECT COUNT(*) FROM nodes"));
    }

    /// <summary>The ids of a node's stored children are listed, and only its own: not other parents', not other plugins'.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ListsTheIdsOfStoredChildren()
    {
        Node otherPlugin = new(new NodeRef(TestData.OtherPlugin, LocalNodeId.Create("c3")), NodeKind.Create("series"), "Other", new NodeRef(TestData.OtherPlugin, LocalNodeId.Create("p")), null, []);
        await this.Store.UpsertAsync([MakeNode("p"), MakeNode("c1", "p"), MakeNode("c2", "p"), MakeNode("q"), MakeNode("c4", "q"), otherPlugin], Ct);

        IReadOnlySet<LocalNodeId> children = await this.Store.ChildIdsAsync(TestData.Ref("p"), Ct);
        IReadOnlySet<LocalNodeId> none = await this.Store.ChildIdsAsync(TestData.Ref("c1"), Ct);

        Assert.Equal(["c1", "c2"], children.Select(id => id.Value).Order(StringComparer.Ordinal));
        Assert.Empty(none);
    }

    /// <summary>A large batch is stored whole.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LargeBatchesAreStored()
    {
        Node[] nodes = [.. Enumerable.Range(0, ContractLimits.MaxNodesPerPage).Select(i => MakeNode($"n{i}", "root"))];

        await this.Store.UpsertAsync(nodes, Ct);

        Assert.Equal((long)ContractLimits.MaxNodesPerPage, await this.ScalarAsync("SELECT COUNT(*) FROM nodes"));
    }

    /// <summary>An empty batch is a no-op.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task EmptyBatchIsANoOp()
    {
        await this.Store.UpsertAsync([], Ct);

        Assert.Equal(0L, await this.ScalarAsync("SELECT COUNT(*) FROM nodes"));
    }

    /// <summary>A batch read returns the stored nodes in the order asked and skips the unknown.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task GetManyReturnsStoredNodesInOrder()
    {
        await this.Store.UpsertAsync([MakeNode("a"), MakeNode("b"), MakeNode("c")], Ct);

        IReadOnlyList<Node> found = await this.Store.GetManyAsync([TestData.Ref("c"), TestData.Ref("missing"), TestData.Ref("a")], detailedOnly: false, Ct);

        Assert.Equal(["c", "a"], found.Select(node => node.Ref.Id.Value));
    }

    /// <summary>A node remembers when it was last described in full; seeing it unchanged in a listing does not renew that, and a change drops it.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DetailedAtIsWhenTheNodeWasLastDescribedInFull()
    {
        Node listed = MakeNode("a");
        Node full = listed with { Props = [new Prop("acme.count", PropValue.FromWholeNumber(5))] };
        NodeRef reference = TestData.Ref("a");
        await this.Store.UpsertAsync([listed], Ct);
        Assert.Empty(await this.Store.DetailedAtAsync([reference], Ct));

        DateTimeOffset described = this.Time.GetUtcNow();
        await this.Store.UpsertDetailedAsync([full], Ct);
        this.Time.Advance(TimeSpan.FromMinutes(5));
        await this.Store.UpsertAsync([full], Ct);
        Assert.Equal(described, (await this.Store.DetailedAtAsync([reference], Ct))[reference]);

        await this.Store.UpsertAsync([listed], Ct);
        Assert.Empty(await this.Store.DetailedAtAsync([reference], Ct));
    }

    /// <summary>Only nodes stored from a full read count as detailed, and a listing that changes one resets it.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DetailedFlagFollowsWhatWasStored()
    {
        Node listed = MakeNode("a");
        await this.Store.UpsertAsync([listed], Ct);
        Assert.Empty(await this.Store.GetManyAsync([TestData.Ref("a")], detailedOnly: true, Ct));

        Node full = listed with { Props = [new Prop("acme.count", PropValue.FromWholeNumber(5))] };
        await this.Store.UpsertDetailedAsync([full], Ct);
        Assert.Single(await this.Store.GetManyAsync([TestData.Ref("a")], detailedOnly: true, Ct));

        // The same node seen in a listing again keeps its detail; a different one loses it.
        await this.Store.UpsertAsync([full], Ct);
        Assert.Single(await this.Store.GetManyAsync([TestData.Ref("a")], detailedOnly: true, Ct));
        await this.Store.UpsertAsync([listed], Ct);
        Assert.Empty(await this.Store.GetManyAsync([TestData.Ref("a")], detailedOnly: true, Ct));
    }
}
