using EMMA.PluginHost.Configuration;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// macOS sandbox scaffolding placeholder (App Sandbox / seatbelt profiles).
/// </summary>
public sealed class MacOsPluginSandboxManager(IOptions<PluginHostOptions> options, ILogger<MacOsPluginSandboxManager> logger)
    : PluginSandboxManagerBase(options, logger)
{
    protected override string PlatformName => "macOS";
}
