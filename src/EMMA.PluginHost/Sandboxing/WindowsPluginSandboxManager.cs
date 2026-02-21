using EMMA.PluginHost.Configuration;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// Windows sandbox scaffolding placeholder (job objects, low-integrity tokens).
/// </summary>
public sealed class WindowsPluginSandboxManager(IOptions<PluginHostOptions> options, ILogger<WindowsPluginSandboxManager> logger)
    : PluginSandboxManagerBase(options, logger)
{
    protected override string PlatformName => "Windows";
}
