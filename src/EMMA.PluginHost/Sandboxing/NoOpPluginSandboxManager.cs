using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// Fallback sandbox manager for unsupported platforms.
/// </summary>
/// <param name="options">The plugin host options.</param>
/// <param name="logger">The logger used for sandbox diagnostics.</param>
public sealed class NoOpPluginSandboxManager(IOptions<PluginHostOptions> options, ILogger<NoOpPluginSandboxManager> logger)
    : PluginSandboxManagerBase(options, logger)
{
    /// <summary>
    /// Gets the platform name used in diagnostics.
    /// </summary>
    protected override string PlatformName => "Unknown";

    /// <summary>
    /// Prepares sandbox resources when no platform-specific sandbox manager is available.
    /// </summary>
    /// <param name="manifest">The plugin manifest.</param>
    /// <param name="pluginRoot">The plugin sandbox root path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes with <see langword="false"/> when no-sandbox fallback is explicitly enabled.</returns>
    protected override Task<bool> PrepareSandboxAsync(
        PluginManifest manifest,
        string pluginRoot,
        CancellationToken cancellationToken)
    {
        if (!Options.AllowNoSandboxFallback)
        {
            throw new InvalidOperationException(
                "NoOpPluginSandboxManager is disabled by default. "
                + "Set PluginHost:AllowNoSandboxFallback=true for explicit development/test usage.");
        }

        if (Logger.IsEnabled(LogLevel.Warning))
        {
            Logger.LogWarning(
                "No-op sandbox active for plugin {PluginId}. Isolation is disabled by explicit configuration.",
                manifest.Id);
        }

        return Task.FromResult(false);
    }
}
