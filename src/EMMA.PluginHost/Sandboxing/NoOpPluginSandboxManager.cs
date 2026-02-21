using EMMA.PluginHost.Configuration;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// Fallback sandbox manager for unsupported platforms.
/// </summary>
public sealed class NoOpPluginSandboxManager(IOptions<PluginHostOptions> options, ILogger<NoOpPluginSandboxManager> logger)
    : PluginSandboxManagerBase(options, logger)
{
    protected override string PlatformName => "Unknown";
}
