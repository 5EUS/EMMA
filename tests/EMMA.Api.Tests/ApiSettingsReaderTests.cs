using EMMA.Api.Configuration;
using EMMA.Api.Security;

using Microsoft.Extensions.Configuration;

using Xunit;

namespace EMMA.Api.Tests;

/// <summary>Tests for reading and validating host configuration.</summary>
public sealed class ApiSettingsReaderTests
{
    private static readonly string Hash = ApiKeys.FormatHash(ApiKeys.Hash("k"));

    /// <summary>With nothing configured the host is locked down: signed plugins only, no keys, default limits.</summary>
    [Fact]
    public void DefaultsAreRestrictive()
    {
        ApiSettings settings = ApiSettingsReader.Read(Config(), allowUnsignedPlugins: false);

        Assert.True(settings.Trust.RequireSigned);
        Assert.Empty(settings.Security.Keys);
        Assert.Equal("x-api-key", settings.Security.KeyHeader);
        Assert.Equal(RateLimitSettings.Default, settings.Security.RateLimit);
        Assert.EndsWith("emma.db", settings.DatabasePath, StringComparison.Ordinal);
    }

    /// <summary>Keys, limits and trust are read, and derived paths sit under the data directory.</summary>
    [Fact]
    public void ConfiguredValuesAreRead()
    {
        ApiSettings settings = ApiSettingsReader.Read(
            Config(
                ("Emma:DataDirectory", "/var/lib/emma"),
                ("Emma:Api:Keys:0:ClientId", "alice"),
                ("Emma:Api:Keys:0:KeyHash", Hash),
                ("Emma:Api:RateLimit:PerClientPermits", "5"),
                ("Emma:Api:RateLimit:PerClientWindowSeconds", "10"),
                ("Emma:Trust:Repositories:0:Id", "main"),
                ("Emma:Trust:Repositories:0:RootKeys:k1", "PEM")),
            allowUnsignedPlugins: false);

        Assert.Equal("alice", Assert.Single(settings.Security.Keys).ClientId);
        Assert.Equal(5, settings.Security.RateLimit.PerClientPermits);
        Assert.Equal(TimeSpan.FromSeconds(10), settings.Security.RateLimit.PerClientWindow);
        Assert.Equal("main", Assert.Single(settings.Trust.Repositories).Id);
        Assert.Equal(Path.GetFullPath("/var/lib/emma/plugins"), settings.InstallRoot);
    }

    /// <summary>Turning signature checks off is refused outside development, so an environment variable cannot do it in production.</summary>
    [Fact]
    public void UnsignedPluginsAreOnlyAllowedInDevelopment()
    {
        IConfiguration config = Config(("Emma:Trust:RequireSigned", "false"));

        Assert.Throws<ApiConfigurationException>(() => ApiSettingsReader.Read(config, allowUnsignedPlugins: false));
        Assert.False(ApiSettingsReader.Read(config, allowUnsignedPlugins: true).Trust.RequireSigned);
    }

    /// <summary>A key that is not a hash is refused, so a raw key pasted into configuration fails the start.</summary>
    [Fact]
    public void RawKeyInConfigurationIsRefused()
    {
        IConfiguration config = Config(("Emma:Api:Keys:0:ClientId", "alice"), ("Emma:Api:Keys:0:KeyHash", "emma_rawkey"));

        ApiConfigurationException error = Assert.Throws<ApiConfigurationException>(() => ApiSettingsReader.Read(config, allowUnsignedPlugins: false));
        Assert.Contains("never configure the key itself", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Two clients may not share an id or a key.</summary>
    [Fact]
    public void DuplicateClientsAreRefused()
    {
        IConfiguration sameId = Config(
            ("Emma:Api:Keys:0:ClientId", "alice"),
            ("Emma:Api:Keys:0:KeyHash", Hash),
            ("Emma:Api:Keys:1:ClientId", "alice"),
            ("Emma:Api:Keys:1:KeyHash", ApiKeys.FormatHash(ApiKeys.Hash("other"))));
        IConfiguration sameKey = Config(
            ("Emma:Api:Keys:0:ClientId", "alice"),
            ("Emma:Api:Keys:0:KeyHash", Hash),
            ("Emma:Api:Keys:1:ClientId", "bob"),
            ("Emma:Api:Keys:1:KeyHash", Hash));

        Assert.Throws<ApiConfigurationException>(() => ApiSettingsReader.Read(sameId, allowUnsignedPlugins: false));
        Assert.Throws<ApiConfigurationException>(() => ApiSettingsReader.Read(sameKey, allowUnsignedPlugins: false));
    }

    /// <summary>Nonsense limits and header names fail the start instead of falling back to a default.</summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The bad value.</param>
    [Theory]
    [InlineData("Emma:Api:RateLimit:PerClientPermits", "0")]
    [InlineData("Emma:Api:RateLimit:PerClientPermits", "many")]
    [InlineData("Emma:Api:RateLimit:GlobalConcurrency", "-1")]
    [InlineData("Emma:Api:KeyHeader", "bad header")]
    [InlineData("Emma:Trust:RequireSigned", "maybe")]
    public void InvalidValuesAreRefused(string key, string value) =>
        Assert.Throws<ApiConfigurationException>(() => ApiSettingsReader.Read(Config((key, value)), allowUnsignedPlugins: true));

    /// <summary>A trusted repository without keys is a mistake worth failing on.</summary>
    [Fact]
    public void TrustedRepositoryNeedsKeys() =>
        Assert.Throws<ApiConfigurationException>(() =>
            ApiSettingsReader.Read(Config(("Emma:Trust:Repositories:0:Id", "main")), allowUnsignedPlugins: false));

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();
}
