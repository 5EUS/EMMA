using EMMA.Domain;
using EMMA.PluginHost.Manifest;
using EMMA.PluginHost.Policy;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.PluginHost.Tests;

/// <summary>Tests for <see cref="ManifestCapabilityPolicyProvider"/>: what a plugin may do follows what it was installed with.</summary>
public sealed class ManifestCapabilityPolicyProviderTests
{
    private static readonly DateTimeOffset Moment = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An enabled plugin gets exactly the hosts and cache permission in its manifest, wildcards included.</summary>
    [Fact]
    public void AnEnabledPluginGetsItsManifestPermissions()
    {
        ManifestCapabilityPolicyProvider provider = new();

        provider.Apply([Installed(TestManifests.Valid(), PluginStatus.Enabled)]);
        CapabilityPolicy policy = provider.GetPolicy(PluginId.Create("com.example.test"));

        Assert.True(policy.AllowsNetwork(TestData.Url("api.example.com")));
        Assert.True(policy.AllowsNetwork(TestData.Url("img.cdn.example.com")));
        Assert.False(policy.AllowsNetwork(TestData.Url("evil.example.net")));
        Assert.True(policy.CacheAllowed);
    }

    /// <summary>Unknown, disabled and quarantined plugins get nothing.</summary>
    [Fact]
    public void UnknownDisabledAndQuarantinedPluginsAreDenied()
    {
        ManifestCapabilityPolicyProvider provider = new();
        provider.Apply(
        [
            Installed(TestManifests.Valid("com.example.off"), PluginStatus.Disabled),
            Installed(TestManifests.Valid("com.example.bad"), PluginStatus.Quarantined),
        ]);

        foreach (string id in new[] { "com.example.off", "com.example.bad", "com.example.never" })
        {
            CapabilityPolicy policy = provider.GetPolicy(PluginId.Create(id));

            Assert.False(policy.AllowsNetwork(TestData.Url("api.example.com")));
            Assert.False(policy.CacheAllowed);
        }
    }

    /// <summary>A plugin whose stored manifest is corrupt, or names a different id than its record, is denied and reported.</summary>
    [Fact]
    public void UnreadableManifestsAreDeniedAndReported()
    {
        ManifestCapabilityPolicyProvider provider = new();
        InstalledPlugin corrupt = Installed(TestManifests.Valid("com.example.corrupt"), PluginStatus.Enabled) with { ManifestJson = "{ not json" };
        InstalledPlugin swapped = Installed(TestManifests.Valid("com.example.other"), PluginStatus.Enabled) with { Id = PluginId.Create("com.example.swapped") };

        IReadOnlyList<PluginId> unreadable = provider.Apply([corrupt, swapped, Installed(TestManifests.Valid(), PluginStatus.Enabled)]);

        Assert.Equal([PluginId.Create("com.example.corrupt"), PluginId.Create("com.example.swapped")], unreadable);
        Assert.False(provider.GetPolicy(PluginId.Create("com.example.swapped")).AllowsNetwork(TestData.Url("api.example.com")));
        Assert.True(provider.GetPolicy(PluginId.Create("com.example.test")).AllowsNetwork(TestData.Url("api.example.com")));
    }

    /// <summary>Refreshing from the registry replaces the whole snapshot: a removed or disabled plugin loses its permissions.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RefreshReplacesTheSnapshot()
    {
        FakePluginRegistry registry = new();
        ManifestCapabilityPolicyProvider provider = new();
        PluginId id = PluginId.Create("com.example.test");
        await registry.UpsertAsync(Installed(TestManifests.Valid(), PluginStatus.Enabled), Ct);

        await provider.RefreshAsync(registry, Ct);
        Assert.True(provider.GetPolicy(id).AllowsNetwork(TestData.Url("api.example.com")));

        await registry.SetStatusAsync(id, PluginStatus.Quarantined, Moment, Ct);
        await provider.RefreshAsync(registry, Ct);

        Assert.False(provider.GetPolicy(id).AllowsNetwork(TestData.Url("api.example.com")));
    }

    /// <summary>The host of a URL setting the manifest names joins that plugin's allow-list, and only that plugin's.</summary>
    [Fact]
    public void ASettingNamedByTheManifestAddsItsHost()
    {
        ManifestCapabilityPolicyProvider provider = new();
        PluginManifest manifest = TestManifests.Valid();
        manifest = manifest with { Permissions = manifest.Permissions with { NetworkSettings = ["instance_url"] } };
        Dictionary<PluginId, IReadOnlyDictionary<string, IReadOnlyList<string>>> settings = new()
        {
            [manifest.Id] = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["instance_url"] = ["https://Invidious.Example.org/"], ["other"] = ["https://not-a-host.example.net"] },
        };

        provider.Apply([Installed(manifest, PluginStatus.Enabled), Installed(TestManifests.Valid("com.example.plain"), PluginStatus.Enabled)], settings);

        Assert.True(provider.GetPolicy(manifest.Id).AllowsNetwork(TestData.Url("invidious.example.org")));
        Assert.True(provider.GetPolicy(manifest.Id).AllowsNetwork(TestData.Url("api.example.com")));
        Assert.False(provider.GetPolicy(manifest.Id).AllowsNetwork(TestData.Url("not-a-host.example.net")));
        Assert.False(provider.GetPolicy(PluginId.Create("com.example.plain")).AllowsNetwork(TestData.Url("invidious.example.org")));
    }

    private static InstalledPlugin Installed(PluginManifest manifest, PluginStatus status) =>
        new(manifest.Id, manifest.Version, status, TestManifests.Json(manifest), null, Moment, Moment);
}
