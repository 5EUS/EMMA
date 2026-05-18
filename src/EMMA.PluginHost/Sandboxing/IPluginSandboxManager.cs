using System.Diagnostics;
using EMMA.PluginHost.Plugins;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// Prepares per-plugin sandbox scaffolding for the current OS.
/// </summary>
public interface IPluginSandboxManager
{
    /// <summary>
    /// Prepares sandbox resources for the supplied plugin.
    /// </summary>
    /// <param name="manifest">The plugin manifest.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resulting sandbox metadata.</returns>
    Task<PluginSandboxResult> PrepareAsync(PluginManifest manifest, CancellationToken cancellationToken);

    /// <summary>
    /// Applies sandbox-specific changes to a process start configuration.
    /// </summary>
    /// <param name="manifest">The plugin manifest.</param>
    /// <param name="startInfo">The process start configuration.</param>
    /// <returns>The updated process start configuration.</returns>
    ProcessStartInfo ApplyToStartInfo(PluginManifest manifest, ProcessStartInfo startInfo);

    /// <summary>
    /// Applies post-start sandbox enforcement to a running plugin process.
    /// </summary>
    /// <param name="manifest">The plugin manifest.</param>
    /// <param name="process">The running process.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when enforcement has been applied.</returns>
    Task EnforceAsync(PluginManifest manifest, Process process, CancellationToken cancellationToken);
}
