using EMMA.PluginHost.Plugins;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// Prepares per-plugin sandbox scaffolding for the current OS.
/// </summary>
public interface IPluginSandboxManager
{
    Task<PluginSandboxResult> PrepareAsync(PluginManifest manifest, CancellationToken cancellationToken);
}
