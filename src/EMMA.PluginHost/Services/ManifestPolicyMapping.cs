using EMMA.Infrastructure.Policy;
using EMMA.PluginHost.Plugins;

namespace EMMA.PluginHost.Services;

internal static class ManifestPolicyMapping
{
    public static ManifestPolicyDefinition ToDefinition(PluginManifest manifest)
    {
        var caps = manifest.Capabilities;
        return new ManifestPolicyDefinition(
            caps?.Cache ?? false,
            caps?.Network,
            manifest.Permissions?.Domains,
            manifest.Permissions?.Paths,
            caps?.FileSystem);
    }
}
