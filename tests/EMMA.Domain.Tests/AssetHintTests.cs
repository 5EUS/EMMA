using Xunit;

namespace EMMA.Domain.Tests;

/// <summary>Tests for <see cref="AssetHint"/>.</summary>
public sealed class AssetHintTests
{
    /// <summary>No hint, and a short printable one, are valid.</summary>
    /// <param name="hint">The hint.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("1080p")]
    [InlineData("Original (best)")]
    public void ValidHints(string? hint) => Assert.True(AssetHint.IsValid(hint));

    /// <summary>An empty hint, one with a control character, and an over-long one are not.</summary>
    /// <param name="hint">The hint.</param>
    [Theory]
    [InlineData("")]
    [InlineData("720p\n")]
    [InlineData("a\0b")]
    public void InvalidHints(string hint) => Assert.False(AssetHint.IsValid(hint));

    /// <summary>The longest hint is accepted and one more character is not.</summary>
    [Fact]
    public void TheLengthIsBounded()
    {
        Assert.True(AssetHint.IsValid(new string('x', AssetHint.MaxLength)));
        Assert.False(AssetHint.IsValid(new string('x', AssetHint.MaxLength + 1)));
    }

    /// <summary>A hint becomes the quality prop the plugin receives; none becomes nothing.</summary>
    [Fact]
    public void BecomesTheQualityProp()
    {
        Assert.Empty(AssetHint.ToProps(null));

        Prop prop = Assert.Single(AssetHint.ToProps("720p"));
        Assert.Equal("emma.quality", prop.Key);
        Assert.Equal("720p", prop.Value.Text);
    }
}
