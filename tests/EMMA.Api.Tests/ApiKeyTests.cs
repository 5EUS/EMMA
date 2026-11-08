using EMMA.Api.Configuration;
using EMMA.Api.Security;

using Xunit;

namespace EMMA.Api.Tests;

/// <summary>Tests for API key generation, hashing and validation.</summary>
public sealed class ApiKeyTests
{
    /// <summary>A generated key validates against its own hash, and the hash round-trips through its text form.</summary>
    [Fact]
    public void GeneratedKeyMatchesItsHash()
    {
        GeneratedApiKey generated = ApiKeys.Generate();

        Assert.True(ApiKeys.TryParseHash(generated.Hash, out byte[]? hash));
        ApiKeyValidator validator = new([new ApiClientKey("alice", hash)]);
        Assert.Equal("alice", validator.Validate(generated.Key)?.ClientId);
        Assert.StartsWith("sha256:", generated.Hash, StringComparison.Ordinal);
    }

    /// <summary>Two generated keys differ, and the key itself is not recoverable from what is configured.</summary>
    [Fact]
    public void GeneratedKeysAreRandomAndNotStored()
    {
        GeneratedApiKey first = ApiKeys.Generate();
        GeneratedApiKey second = ApiKeys.Generate();

        Assert.NotEqual(first.Key, second.Key);
        Assert.DoesNotContain(first.Key, first.Hash, StringComparison.Ordinal);
        Assert.True(first.Key.Length >= 40);
    }

    /// <summary>Only a well-formed hash is accepted.</summary>
    /// <param name="text">The candidate text.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain-text-key")]
    [InlineData("sha256:abc")]
    [InlineData("sha1:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha256:zz00000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha256:00000000000000000000000000000000000000000000000000000000000000000")]
    public void MalformedHashesAreRejected(string? text) => Assert.False(ApiKeys.TryParseHash(text, out _));

    /// <summary>The right client is found among many, and every wrong key is refused.</summary>
    [Fact]
    public void ValidatorPicksTheMatchingClient()
    {
        ApiKeyValidator validator = new(
        [
            new ApiClientKey("alice", ApiKeys.Hash("key-a")),
            new ApiClientKey("bob", ApiKeys.Hash("key-b")),
            new ApiClientKey("carol", ApiKeys.Hash("key-c")),
        ]);

        Assert.Equal("bob", validator.Validate("key-b")?.ClientId);
        Assert.Null(validator.Validate("key-B"));
        Assert.Null(validator.Validate("key-b "));
        Assert.Null(validator.Validate(string.Empty));
        Assert.Null(validator.Validate(null));
    }

    /// <summary>An oversized key is refused before it is hashed.</summary>
    [Fact]
    public void OversizedKeyIsRefused()
    {
        ApiKeyValidator validator = new([new ApiClientKey("alice", ApiKeys.Hash(new string('x', ApiKeys.MaxKeyLength + 1)))]);

        Assert.Null(validator.Validate(new string('x', ApiKeys.MaxKeyLength + 1)));
    }

    /// <summary>With no keys configured nothing authenticates.</summary>
    [Fact]
    public void NoKeysMeansNoAccess() => Assert.Null(new ApiKeyValidator([]).Validate("anything"));
}
