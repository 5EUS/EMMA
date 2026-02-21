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

    public Task<PluginSandboxResult> PrepareAsync(PluginManifest manifest, CancellationToken cancellationToken)
    {
        var root = _options.SandboxRootDirectory;
        var pluginRoot = Path.Combine(root, manifest.Id);
        Directory.CreateDirectory(pluginRoot);

        if (_options.SandboxEnabled && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "{Platform} sandbox enforcement not implemented yet. Prepared {Path}.",
                PlatformName,
                pluginRoot);
        }

        return Task.FromResult(new PluginSandboxResult(pluginRoot, _options.SandboxEnabled, false));
    }

    protected abstract string PlatformName { get; }
}
