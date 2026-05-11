using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// Android sandbox stub (mobile environments are sandboxed by the OS).
/// </summary>
/// <param name="options">The plugin host options.</param>
/// <param name="logger">The logger used for sandbox diagnostics.</param>
public sealed class AndroidPluginSandboxManager(IOptions<PluginHostOptions> options, ILogger<AndroidPluginSandboxManager> logger)
    : PluginSandboxManagerBase(options, logger)
{
    /// <summary>
    /// Gets the platform name used in diagnostics.
    /// </summary>
    protected override string PlatformName => "Android";

    /// <summary>
    /// Prepares sandbox resources for Android-hosted plugins.
    /// </summary>
    /// <param name="manifest">The plugin manifest.</param>
    /// <param name="pluginRoot">The plugin sandbox root path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> because Android already provides OS-level sandboxing.</returns>
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
