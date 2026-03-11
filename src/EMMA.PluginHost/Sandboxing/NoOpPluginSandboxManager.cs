using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// Fallback sandbox manager for unsupported platforms.
/// </summary>
public sealed class NoOpPluginSandboxManager(IOptions<PluginHostOptions> options, ILogger<NoOpPluginSandboxManager> logger)
    : PluginSandboxManagerBase(options, logger)
{
    protected override string PlatformName => "Unknown";

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
