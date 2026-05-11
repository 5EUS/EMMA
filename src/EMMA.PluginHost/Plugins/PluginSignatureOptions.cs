namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Configures how plugin signature verification is enforced and where trust material is loaded from.
/// </summary>
public sealed class PluginSignatureOptions
{
    /// <summary>
    /// Gets a value indicating whether unsigned plugins should be rejected.
    /// </summary>
    public bool RequireSignedPlugins { get; init; } = false; // TODO default to true in a future major version

    /// <summary>
    /// Gets the directory that contains repository delegation metadata files.
    /// </summary>
    public string? DelegationDirectory { get; init; }

    /// <summary>
    /// Gets the directory that contains trusted repository root public keys.
    /// </summary>
    public string? RootKeyDirectory { get; init; }
}
