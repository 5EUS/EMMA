using Xunit;

namespace EMMA.Domain.Tests;

/// <summary>Tests for <see cref="PropValue"/>.</summary>
public sealed class PropValueTests
{
    /// <summary>Each factory sets its own kind and payload.</summary>
    [Fact]
    public void FactoriesSetKindAndPayload()
    {
        Assert.Equal(PropValueKind.Text, PropValue.FromText("x").Kind);
        Assert.Equal(42, PropValue.FromWholeNumber(42).WholeNumber);
        Assert.Equal(1.5, PropValue.FromNumber(1.5).Number);
        Assert.True(PropValue.FromFlag(true).Flag);
        Assert.False(PropValue.FromFlag(false).Flag);
        Assert.Equal(PropValueKind.Timestamp, PropValue.FromTimestamp(1000).Kind);
        Uri link = new UriBuilder("https", "example.com", -1, "/a").Uri;
        Assert.Equal(link.AbsoluteUri, PropValue.FromUrl(link).Text);
    }

    /// <summary>List values compare by content, not by reference.</summary>
    [Fact]
    public void TextListsCompareByContent()
    {
        PropValue first = PropValue.FromTextList(["a", "b"]);
        PropValue second = PropValue.FromTextList(["a", "b"]);
        PropValue different = PropValue.FromTextList(["a", "c"]);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, different);
    }

    /// <summary>The list is copied so later changes to the source do not leak in.</summary>
    [Fact]
    public void TextListIsCopied()
    {
        List<string> source = ["a"];
        PropValue value = PropValue.FromTextList(source);

        source.Add("b");

        Assert.Single(value.TextList);
    }

    /// <summary>Values of different kinds are never equal, even with the same number.</summary>
    [Fact]
    public void DifferentKindsAreNotEqual() =>
        Assert.NotEqual(PropValue.FromWholeNumber(1), PropValue.FromTimestamp(1));
}
