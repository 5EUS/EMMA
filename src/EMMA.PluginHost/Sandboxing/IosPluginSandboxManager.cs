using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// iOS sandbox stub (mobile environments are sandboxed by the OS).
/// </summary>
public sealed class IosPluginSandboxManager(IOptions<PluginHostOptions> options, ILogger<IosPluginSandboxManager> logger)
    : PluginSandboxManagerBase(options, logger)
{
    protected override string PlatformName => "iOS";

    protected override Task<bool> PrepareSandboxAsync(
        PluginManifest manifest,
        string pluginRoot,
        CancellationToken cancellationToken)
    {
        // iOS enforces sandboxing at the OS level; no extra process wrapper.
        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation("iOS environment provides OS-level sandboxing.");
        }

        return Task.FromResult(true);
    }
}
