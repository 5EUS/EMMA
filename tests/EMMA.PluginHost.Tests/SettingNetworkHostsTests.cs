using EMMA.PluginHost.Manifest;
using EMMA.PluginHost.Policy;

using Xunit;

namespace EMMA.PluginHost.Tests;

/// <summary>Tests for <see cref="SettingNetworkHosts"/>: a user's own base URL reaches the allow-list, and nothing else does.</summary>
public sealed class SettingNetworkHostsTests
{
    private static readonly ManifestPermissions WithSetting = new(["api.example.com"], false, ["instance_url"]);

    /// <summary>Only an http or https URL with a host and no credentials yields a host, lower-cased.</summary>
    /// <param name="value">The setting value.</param>
    /// <param name="expected">The host it yields, or <see langword="null"/>.</param>
    [Theory]
    [InlineData("https://Invidious.Example.org/some/path", "invidious.example.org")]
    [InlineData("http://192.168.1.20:3000", "192.168.1.20")]
    [InlineData("http://localhost:3000/", "localhost")]
    [InlineData("ftp://example.org", null)]
    [InlineData("https://user:pw@example.org", null)]
    [InlineData("not a url", null)]
    [InlineData("", null)]
    public void HostOfAcceptsOnlyPlainWebUrls(string value, string? expected) =>
        Assert.Equal(expected, SettingNetworkHosts.HostOf(value));

    /// <summary>The effective list is the manifest's plus the setting's host, without repeats, and a bad value adds nothing.</summary>
    [Fact]
    public void EffectiveAddsTheSettingsHost()
    {
        Dictionary<string, IReadOnlyList<string>> settings = new(StringComparer.Ordinal) { ["instance_url"] = ["https://api.example.com"] };
        Assert.Equal(["api.example.com"], SettingNetworkHosts.Effective(WithSetting, settings));

        settings["instance_url"] = ["https://vid.example.org"];
        Assert.Equal(["api.example.com", "vid.example.org"], SettingNetworkHosts.Effective(WithSetting, settings));

        settings["instance_url"] = ["javascript:alert(1)"];
        Assert.Equal(["api.example.com"], SettingNetworkHosts.Effective(WithSetting, settings));
    }

    /// <summary>A plugin that names no URL setting never gains a host, whatever settings it holds.</summary>
    [Fact]
    public void APluginWithoutUrlSettingsIsUnchanged()
    {
        ManifestPermissions fixedHosts = new(["api.example.com"], false);
        Dictionary<string, IReadOnlyList<string>> settings = new(StringComparer.Ordinal) { ["instance_url"] = ["https://vid.example.org"] };

        Assert.Equal(["api.example.com"], SettingNetworkHosts.Effective(fixedHosts, settings));
    }

    /// <summary>The local-network toggle counts only for a plugin with URL settings, and only when it is exactly true.</summary>
    [Fact]
    public void LocalNetworkNeedsTheToggleAndAUrlSetting()
    {
        Dictionary<string, IReadOnlyList<string>> on = new(StringComparer.Ordinal) { [SettingNetworkHosts.AllowLocalNetworkKey] = ["true"] };
        Dictionary<string, IReadOnlyList<string>> off = new(StringComparer.Ordinal) { [SettingNetworkHosts.AllowLocalNetworkKey] = ["false"] };

        Assert.True(SettingNetworkHosts.AllowsLocalNetwork(WithSetting, on));
        Assert.False(SettingNetworkHosts.AllowsLocalNetwork(WithSetting, off));
        Assert.False(SettingNetworkHosts.AllowsLocalNetwork(WithSetting, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)));
        Assert.False(SettingNetworkHosts.AllowsLocalNetwork(new ManifestPermissions(["api.example.com"], false), on));
    }
}
