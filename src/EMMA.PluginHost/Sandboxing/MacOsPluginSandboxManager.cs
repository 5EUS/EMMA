using System.Diagnostics;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// macOS sandbox integration. App Sandbox is enforced by app bundles.
/// </summary>
public sealed class MacOsPluginSandboxManager(IOptions<PluginHostOptions> options, ILogger<MacOsPluginSandboxManager> logger)
    : PluginSandboxManagerBase(options, logger)
{
    protected override string PlatformName => "macOS";

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

    public override ProcessStartInfo ApplyToStartInfo(PluginManifest manifest, ProcessStartInfo startInfo)
    {
        return startInfo;
    }
}
