namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Verifies the authenticity and integrity of plugin manifests.
/// </summary>
public interface IPluginSignatureVerifier
{
    /// <summary>
    /// Verifies the supplied plugin manifest.
    /// </summary>
    /// <param name="manifest">The plugin manifest to verify.</param>
    /// <param name="reason">When verification fails, receives a descriptive failure reason.</param>
    /// <returns><see langword="true"/> when the manifest is accepted; otherwise, <see langword="false"/>.</returns>
    bool Verify(PluginManifest manifest, out string? reason);
}
