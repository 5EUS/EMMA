using EMMA.PluginHost.Configuration;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// Linux sandbox scaffolding placeholder (cgroups, seccomp, namespaces).
/// </summary>
public sealed class LinuxPluginSandboxManager(IOptions<PluginHostOptions> options, ILogger<LinuxPluginSandboxManager> logger)
    : PluginSandboxManagerBase(options, logger)
{
    protected override string PlatformName => "Linux";
}
