using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Application.Tests;

/// <summary>Tests for contract validation of what a plugin returns.</summary>
public sealed class NodeValidatorTests
{
    private static readonly PluginInfo Info = TestData.Info();

    /// <summary>A well-formed page becomes host nodes owned by the plugin, with the page's context as parent.</summary>
    [Fact]
    public void ValidPageIsNamespacedUnderThePlugin()
    {
        PluginNodePage raw = TestData.RawPage("next", TestData.RawNode("c1", "chapter"), TestData.RawNode("c2", "chapter"));

        Outcome<NodePage> page = NodeValidator.ValidatePage(TestData.Plugin, Info, LocalNodeId.Create("s1"), raw);

        Assert.True(page.IsSuccess);
        Assert.Equal("next", page.Value.NextCursor);
        Assert.Equal([TestData.Ref("c1"), TestData.Ref("c2")], page.Value.Items.Select(node => node.Ref));
        Assert.All(page.Value.Items, node => Assert.Equal(TestData.Ref("s1"), node.Parent));
    }

    /// <summary>A parent the node names itself wins over the page's context.</summary>
    [Fact]
    public void ExplicitParentWinsOverContext()
    {
        PluginNodePage raw = TestData.RawPage(null, TestData.RawNode("c1", "chapter", parent: "other"));

        Outcome<NodePage> page = NodeValidator.ValidatePage(TestData.Plugin, Info, LocalNodeId.Create("s1"), raw);

        Assert.Equal(TestData.Ref("other"), page.Value.Items[0].Parent);
    }

    /// <summary>Nodes of a kind the plugin never declared are rejected and the message names the plugin and kind.</summary>
    [Fact]
    public void UndeclaredKindIsRejected()
    {
        PluginNodePage raw = TestData.RawPage(null, TestData.RawNode("x", "podcast"));

        Outcome<NodePage> page = NodeValidator.ValidatePage(TestData.Plugin, Info, null, raw);

        Assert.Equal(ErrorCode.Internal, page.Error?.Code);
        Assert.Contains("podcast", page.Error?.Message, StringComparison.Ordinal);
        Assert.Contains("com.example.test", page.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>Malformed ids, kinds and titles are rejected.</summary>
    /// <param name="id">The node id.</param>
    /// <param name="kind">The node kind.</param>
    /// <param name="title">The node title.</param>
    [Theory]
    [InlineData("", "series", "T")]
    [InlineData("a", "Series", "T")]
    [InlineData("a", "series", "")]
    [InlineData("a", "series", "   ")]
    public void MalformedNodeIsRejected(string id, string kind, string title)
    {
        PluginNodePage raw = TestData.RawPage(null, new PluginNode(id, kind, title, null, null, []));

        Assert.False(NodeValidator.ValidatePage(TestData.Plugin, Info, null, raw).IsSuccess);
    }

    /// <summary>A response with the same node twice is rejected.</summary>
    [Fact]
    public void DuplicateIdsAreRejected()
    {
        PluginNodePage raw = TestData.RawPage(null, TestData.RawNode("a"), TestData.RawNode("a"));

        Outcome<NodePage> page = NodeValidator.ValidatePage(TestData.Plugin, Info, null, raw);

        Assert.Contains("twice", page.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>A page over the size limit is rejected.</summary>
    [Fact]
    public void OversizedPageIsRejected()
    {
        PluginNode[] nodes = [.. Enumerable.Range(0, ContractLimits.MaxNodesPerPage + 1).Select(i => TestData.RawNode($"n{i}"))];

        Assert.False(NodeValidator.ValidatePage(TestData.Plugin, Info, null, TestData.RawPage(null, nodes)).IsSuccess);
    }

    /// <summary>An empty or oversized cursor is rejected.</summary>
    /// <param name="cursor">The cursor.</param>
    [Theory]
    [InlineData("")]
    public void BadCursorIsRejected(string cursor) =>
        Assert.False(NodeValidator.ValidatePage(TestData.Plugin, Info, null, TestData.RawPage(cursor)).IsSuccess);

    /// <summary>A cursor over the byte limit is rejected.</summary>
    [Fact]
    public void OversizedCursorIsRejected() =>
        Assert.False(NodeValidator.ValidatePage(
            TestData.Plugin,
            Info,
            null,
            TestData.RawPage(new string('c', ContractLimits.MaxCursorBytes + 1))).IsSuccess);

    /// <summary>Props with a malformed key, a repeated key, or too many entries are rejected.</summary>
    [Fact]
    public void BadPropsAreRejected()
    {
        Prop good = new("emma.synopsis", PropValue.FromText("x"));
        static PluginNode MakeNode(params Prop[] props) => new("a", "series", "T", null, null, props);

        Assert.False(NodeValidator.ValidateNodes(TestData.Plugin, Info, null, [MakeNode(new Prop("Bad Key", PropValue.FromText("x")))]).IsSuccess);
        Assert.False(NodeValidator.ValidateNodes(TestData.Plugin, Info, null, [MakeNode(good, good)]).IsSuccess);
        Assert.False(NodeValidator.ValidateNodes(
            TestData.Plugin,
            Info,
            null,
            [MakeNode([.. Enumerable.Range(0, ContractLimits.MaxPropsPerNode + 1).Select(i => new Prop($"acme.p{i}", PropValue.FromFlag(true)))])]).IsSuccess);
        Assert.True(NodeValidator.ValidateNodes(TestData.Plugin, Info, null, [MakeNode(good)]).IsSuccess);
    }

    /// <summary>An unusable thumbnail is dropped instead of discarding the whole page.</summary>
    /// <param name="thumbnail">The thumbnail text.</param>
    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path.png")]
    [InlineData("file:///etc/passwd")]
    public void BadThumbnailIsDroppedNotFatal(string thumbnail)
    {
        PluginNode raw = new("a", "series", "T", null, thumbnail, []);

        Outcome<IReadOnlyList<Node>> nodes = NodeValidator.ValidateNodes(TestData.Plugin, Info, null, [raw]);

        Assert.Null(nodes.Value[0].Thumbnail);
    }

    /// <summary>A good thumbnail is kept as an absolute URL.</summary>
    [Fact]
    public void GoodThumbnailIsKept()
    {
        Uri url = TestData.Url("img.example.com", "/a.png");
        PluginNode raw = new("a", "series", "T", null, url.AbsoluteUri, []);

        Assert.Equal(url, NodeValidator.ValidateNodes(TestData.Plugin, Info, null, [raw]).Value[0].Thumbnail);
    }
}
