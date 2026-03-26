using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// Android sandbox stub (mobile environments are sandboxed by the OS).
/// </summary>
public sealed class AndroidPluginSandboxManager(IOptions<PluginHostOptions> options, ILogger<AndroidPluginSandboxManager> logger)
    : PluginSandboxManagerBase(options, logger)
{
    protected override string PlatformName => "Android";

    protected override Task<bool> PrepareSandboxAsync(
        PluginManifest manifest,
        string pluginRoot,
        CancellationToken cancellationToken)
    {
        // Android enforces sandboxing at the OS level; no extra process wrapper.
        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation("Android environment provides OS-level sandboxing.");
        }

        return Task.FromResult(true);
    }
}
