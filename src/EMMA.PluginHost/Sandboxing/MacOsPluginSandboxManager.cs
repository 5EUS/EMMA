using System.Diagnostics;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// macOS sandbox integration. App Sandbox is enforced by app bundles.
/// </summary>
/// <param name="options">The plugin host options.</param>
/// <param name="logger">The logger used for sandbox diagnostics.</param>
public sealed class MacOsPluginSandboxManager(IOptions<PluginHostOptions> options, ILogger<MacOsPluginSandboxManager> logger)
    : PluginSandboxManagerBase(options, logger)
{
    /// <summary>
    /// Gets the platform name used in diagnostics.
    /// </summary>
    protected override string PlatformName => "macOS";

    /// <summary>
    /// Prepares sandbox resources for macOS-hosted plugins.
    /// </summary>
    /// <param name="manifest">The plugin manifest.</param>
    /// <param name="pluginRoot">The plugin sandbox root path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> because macOS App Sandbox is expected to enforce isolation.</returns>
    protected override Task<bool> PrepareSandboxAsync(
        PluginManifest manifest,
        string pluginRoot,
        CancellationToken cancellationToken)
    {
        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation("macOS App Sandbox expected; no host-side sandboxing applied.");
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// Returns the supplied start info unchanged because host-side macOS sandbox wrapping is not applied.
    /// </summary>
    /// <param name="manifest">The plugin manifest.</param>
    /// <param name="startInfo">The process start configuration.</param>
    /// <returns>The original process start configuration.</returns>
    public override ProcessStartInfo ApplyToStartInfo(PluginManifest manifest, ProcessStartInfo startInfo)
    {
        return startInfo;
    }
}
