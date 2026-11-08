using Xunit;

namespace EMMA.Domain.Tests;

/// <summary>Tests for the identity value types.</summary>
public sealed class IdentityTests
{
    /// <summary>Valid plugin ids are accepted.</summary>
    /// <param name="text">The candidate id.</param>
    [Theory]
    [InlineData("com.example.manga")]
    [InlineData("org.acme.video-hub")]
    [InlineData("a.b")]
    public void PluginIdAcceptsReverseDnsNames(string text) =>
        Assert.True(PluginId.TryCreate(text, out PluginId id) && string.Equals(id.Value, text, StringComparison.Ordinal));

    /// <summary>Malformed plugin ids are rejected, including ones containing a colon.</summary>
    /// <param name="text">The candidate id.</param>
    [Theory]
    [InlineData("")]
    [InlineData("example")]
    [InlineData("Com.Example")]
    [InlineData("com.example:evil")]
    [InlineData("com..example")]
    [InlineData(".com.example")]
    public void PluginIdRejectsMalformedNames(string text) =>
        Assert.False(PluginId.TryCreate(text, out _));

    /// <summary>A null plugin id is rejected rather than throwing.</summary>
    [Fact]
    public void PluginIdRejectsNull() => Assert.False(PluginId.TryCreate(null, out _));

    /// <summary>Create throws for an invalid plugin id.</summary>
    [Fact]
    public void PluginIdCreateThrows() => Assert.Throws<ArgumentException>(() => PluginId.Create("Nope"));

    /// <summary>Local ids are limited by UTF-8 byte length, not character count.</summary>
    [Fact]
    public void LocalNodeIdLimitsBytesNotCharacters()
    {
        string withinLimit = new('a', ContractLimits.MaxNodeIdBytes);
        string overByteLimit = new('é', (ContractLimits.MaxNodeIdBytes / 2) + 1);

        Assert.True(LocalNodeId.TryCreate(withinLimit, out _));
        Assert.True(overByteLimit.Length <= ContractLimits.MaxNodeIdBytes);
        Assert.False(LocalNodeId.TryCreate(overByteLimit, out _));
    }

    /// <summary>Empty local ids are rejected.</summary>
    [Fact]
    public void LocalNodeIdRejectsEmpty() => Assert.False(LocalNodeId.TryCreate(string.Empty, out _));

    /// <summary>A node reference round-trips through its text form, even when the local id has colons.</summary>
    [Fact]
    public void NodeRefRoundTripsThroughText()
    {
        NodeRef original = new(PluginId.Create("com.example.manga"), LocalNodeId.Create("series:42/ch:7"));

        string text = original.ToString();
        NodeRef parsed = NodeRef.Parse(text);

        Assert.Equal("com.example.manga:series:42/ch:7", text);
        Assert.Equal(original, parsed);
    }

    /// <summary>Text without a valid plugin part or local part is not a reference.</summary>
    /// <param name="text">The candidate text.</param>
    [Theory]
    [InlineData("")]
    [InlineData("nocolon")]
    [InlineData(":local")]
    [InlineData("com.example.manga:")]
    [InlineData("Bad.Plugin:local")]
    public void NodeRefRejectsMalformedText(string text) => Assert.False(NodeRef.TryParse(text, out _));

    /// <summary>Parse throws a format error for bad text.</summary>
    [Fact]
    public void NodeRefParseThrows() => Assert.Throws<FormatException>(() => NodeRef.Parse("nope"));

    /// <summary>The same local id under different plugins is a different node.</summary>
    [Fact]
    public void SameLocalIdUnderDifferentPluginsDiffers()
    {
        LocalNodeId local = LocalNodeId.Create("1");

        Assert.NotEqual(
            new NodeRef(PluginId.Create("com.a.one"), local),
            new NodeRef(PluginId.Create("com.b.two"), local));
    }

    /// <summary>Kinds and roles accept lower-case dotted tokens and reject everything else.</summary>
    /// <param name="text">The candidate token.</param>
    /// <param name="valid">Whether it should be accepted.</param>
    [Theory]
    [InlineData("manga", true)]
    [InlineData("manga.chapter", true)]
    [InlineData("recipe.step", true)]
    [InlineData("Manga", false)]
    [InlineData("manga chapter", false)]
    [InlineData("", false)]
    [InlineData(".chapter", false)]
    public void KindAndRoleTokensAreValidated(string text, bool valid)
    {
        Assert.Equal(valid, NodeKind.TryCreate(text, out _));
        Assert.Equal(valid, AssetRole.TryCreate(text, out _));
    }

    /// <summary>User ids are trimmed and blank ones rejected.</summary>
    [Fact]
    public void UserIdTrimsAndRejectsBlank()
    {
        Assert.True(UserId.TryCreate("  alice ", out UserId id));
        Assert.Equal("alice", id.Value);
        Assert.False(UserId.TryCreate("   ", out _));
    }
}
