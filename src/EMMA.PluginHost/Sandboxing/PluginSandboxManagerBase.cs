using System.Diagnostics;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// Shared scaffolding behavior for platform-specific sandbox managers.
/// </summary>
public abstract class PluginSandboxManagerBase(IOptions<PluginHostOptions> options, ILogger logger) : IPluginSandboxManager
{
    private readonly PluginHostOptions _options = options.Value;
    private readonly ILogger _logger = logger;

    protected PluginHostOptions Options => _options;
    protected ILogger Logger => _logger;

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

    public virtual ProcessStartInfo ApplyToStartInfo(PluginManifest manifest, ProcessStartInfo startInfo) => startInfo;

    public virtual Task EnforceAsync(PluginManifest manifest, Process process, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected string GetPluginRoot(PluginManifest manifest)
    {
        var root = _options.SandboxRootDirectory;
        return Path.Combine(root, manifest.Id);
    }

    protected virtual Task<bool> PrepareSandboxAsync(
        PluginManifest manifest,
        string pluginRoot,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(false);
    }

    protected abstract string PlatformName { get; }
}
