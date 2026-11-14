namespace EMMA.PluginHost.Manifest;

/// <summary>What the plugin may ask the host for. There is deliberately no filesystem permission.</summary>
/// <param name="Network">Exact host names or <c>*.example.com</c> wildcards; empty means no network.</param>
/// <param name="Cache">Whether the host cache is available.</param>
/// <param name="NetworkSettings">
/// Keys of the plugin's own text settings that hold a base URL the user chooses (a self-hosted instance). The host of each
/// value is added to the allow-list for that plugin only. Empty for a plugin whose hosts are all fixed.
/// </param>
public sealed record ManifestPermissions(IReadOnlyList<string> Network, bool Cache, IReadOnlyList<string> NetworkSettings)
{
    /// <summary>Initializes a new instance of the <see cref="ManifestPermissions"/> class with no setting-derived hosts.</summary>
    /// <param name="network">Exact host names or wildcards.</param>
    /// <param name="cache">Whether the host cache is available.</param>
    public ManifestPermissions(IReadOnlyList<string> network, bool cache)
        : this(network, cache, [])
    {
    }
}
