using Xunit;

namespace EMMA.Domain.Tests;

/// <summary>Tests for <see cref="CapabilityPolicy"/>.</summary>
public sealed class CapabilityPolicyTests
{
    /// <summary>An exact host is allowed regardless of case, path or port.</summary>
    [Fact]
    public void ExactHostIsAllowed()
    {
        CapabilityPolicy policy = new(["api.example.com"], cacheAllowed: false);

        Assert.True(policy.AllowsNetwork(Url("https", "api.example.com", "/v1/x")));
        Assert.True(policy.AllowsNetwork(new UriBuilder("https", "API.Example.com", 8443).Uri));
    }

    /// <summary>A host that merely shares a prefix or suffix with an allowed one is denied.</summary>
    /// <param name="host">The lookalike host.</param>
    [Theory]
    [InlineData("api.example.com.evil.net")]
    [InlineData("evilapi.example.com")]
    [InlineData("example.com")]
    [InlineData("xapi.example.com")]
    public void LookalikeHostsAreDenied(string host)
    {
        CapabilityPolicy policy = new(["api.example.com"], cacheAllowed: false);

        Assert.False(policy.AllowsNetwork(Url("https", host, "/")));
    }

    /// <summary>A wildcard matches subdomains at any depth but not the apex, nor a lookalike suffix.</summary>
    [Fact]
    public void WildcardMatchesSubdomainsOnly()
    {
        CapabilityPolicy policy = new(["*.cdn.example.com"], cacheAllowed: false);

        Assert.True(policy.AllowsNetwork(Url("https", "a.cdn.example.com", "/")));
        Assert.True(policy.AllowsNetwork(Url("https", "a.b.cdn.example.com", "/")));
        Assert.False(policy.AllowsNetwork(Url("https", "cdn.example.com", "/")));
        Assert.False(policy.AllowsNetwork(Url("https", "evilcdn.example.com", "/")));
        Assert.False(policy.AllowsNetwork(Url("https", "a.cdn.example.com.evil.net", "/")));
    }

    /// <summary>Wildcards never match IP literals.</summary>
    [Fact]
    public void WildcardsDoNotMatchIpLiterals()
    {
        CapabilityPolicy policy = new(["*.0.1"], cacheAllowed: false);

        Assert.False(policy.AllowsNetwork(Url("http", "127.0.0.1", "/")));
    }

    /// <summary>Only http and https are ever allowed, even for an allowed host.</summary>
    /// <param name="scheme">The URL scheme.</param>
    [Theory]
    [InlineData("ftp")]
    [InlineData("file")]
    [InlineData("ws")]
    public void NonHttpSchemesAreDenied(string scheme)
    {
        CapabilityPolicy policy = new(["api.example.com"], cacheAllowed: false);

        Uri candidate = new UriBuilder(scheme, "api.example.com", -1, "/file").Uri;

        Assert.False(policy.AllowsNetwork(candidate));
    }

    /// <summary>Deny-all grants nothing, and cache is a separate switch.</summary>
    [Fact]
    public void DenyAllGrantsNothing()
    {
        Assert.False(CapabilityPolicy.DenyAll.AllowsNetwork(Url("https", "example.com", "/")));
        Assert.False(CapabilityPolicy.DenyAll.CacheAllowed);
        Assert.True(new CapabilityPolicy([], cacheAllowed: true).CacheAllowed);
    }

    private static Uri Url(string scheme, string host, string path) => new UriBuilder(scheme, host, -1, path).Uri;
}
