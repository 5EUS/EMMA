namespace EMMA.PluginHost.Manifest;

/// <summary>What the plugin may ask the host for. There is deliberately no filesystem permission.</summary>
/// <param name="Network">Exact host names or <c>*.example.com</c> wildcards; empty means no network.</param>
/// <param name="Cache">Whether the host cache is available.</param>
public sealed record ManifestPermissions(IReadOnlyList<string> Network, bool Cache);
