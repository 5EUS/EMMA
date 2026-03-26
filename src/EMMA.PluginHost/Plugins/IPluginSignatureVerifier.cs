namespace EMMA.PluginHost.Plugins;

public interface IPluginSignatureVerifier
{
    bool Verify(PluginManifest manifest, out string? reason);
}
