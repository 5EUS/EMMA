using System.Diagnostics;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// Shared scaffolding behavior for platform-specific sandbox managers.
/// </summary>
/// <param name="options">The plugin host options.</param>
/// <param name="logger">The logger used for sandbox diagnostics.</param>
public abstract class PluginSandboxManagerBase(IOptions<PluginHostOptions> options, ILogger logger) : IPluginSandboxManager
{
    private readonly PluginHostOptions _options = options.Value;
    private readonly ILogger _logger = logger;

    /// <summary>
    /// Gets the resolved plugin host options.
    /// </summary>
    protected PluginHostOptions Options => _options;

    /// <summary>
    /// Gets the logger used by the sandbox manager.
    /// </summary>
    protected ILogger Logger => _logger;

    /// <summary>
    /// Prepares sandbox resources for the supplied plugin.
    /// </summary>
    /// <param name="manifest">The plugin manifest.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resulting sandbox metadata.</returns>
    public async Task<PluginSandboxResult> PrepareAsync(PluginManifest manifest, CancellationToken cancellationToken)
    {
        var pluginRoot = GetPluginRoot(manifest);
        Directory.CreateDirectory(pluginRoot);

        var enforced = _options.SandboxEnabled && await PrepareSandboxAsync(manifest, pluginRoot, cancellationToken);

        if (_options.SandboxEnabled && !enforced && _logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "{Platform} sandbox enforcement not available. Prepared {Path}.",
                PlatformName,
                pluginRoot);
        }

        return new PluginSandboxResult(pluginRoot, _options.SandboxEnabled, enforced);
    }

    /// <summary>
    /// Applies sandbox-specific changes to a process start configuration.
    /// </summary>
    /// <param name="manifest">The plugin manifest.</param>
    /// <param name="startInfo">The process start configuration.</param>
    /// <returns>The updated process start configuration.</returns>
    public virtual ProcessStartInfo ApplyToStartInfo(PluginManifest manifest, ProcessStartInfo startInfo) => startInfo;

    /// <summary>
    /// Applies post-start sandbox enforcement to a running plugin process.
    /// </summary>
    /// <param name="manifest">The plugin manifest.</param>
    /// <param name="process">The running process.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when enforcement has been applied.</returns>
    public virtual Task EnforceAsync(PluginManifest manifest, Process process, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the filesystem root assigned to the supplied plugin sandbox.
    /// </summary>
    /// <param name="manifest">The plugin manifest.</param>
    /// <returns>The plugin sandbox root path.</returns>
    protected string GetPluginRoot(PluginManifest manifest)
    {
        var root = _options.SandboxRootDirectory;
        return Path.Combine(root, manifest.Id);
    }

    /// <summary>
    /// Prepares any platform-specific sandbox resources for the supplied plugin.
    /// </summary>
    /// <param name="manifest">The plugin manifest.</param>
    /// <param name="pluginRoot">The plugin sandbox root path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when sandbox enforcement is available; otherwise, <see langword="false"/>.</returns>
    protected virtual Task<bool> PrepareSandboxAsync(
        PluginManifest manifest,
        string pluginRoot,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(false);
    }

    /// <summary>
    /// Gets the platform name used in sandbox diagnostics.
    /// </summary>
    protected abstract string PlatformName { get; }
}
