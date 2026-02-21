using EMMA.Contracts.Plugins;
using EMMA.PluginHost.Plugins;
using Grpc.Core;

namespace EMMA.PluginHost.Services;

/// <summary>
/// gRPC control surface for plugin health and capability discovery.
/// </summary>
public sealed class PluginControlService(PluginRegistry registry) : PluginControl.PluginControlBase
{
    private readonly PluginRegistry _registry = registry;

    /// <summary>
    /// Returns basic health information for the host.
    /// </summary>
    public override Task<HealthResponse> GetHealth(HealthRequest request, ServerCallContext context)
    {
        var version = typeof(PluginControlService).Assembly.GetName().Version?.ToString() ?? "unknown";

        return Task.FromResult(new HealthResponse
        {
            Status = "ok",
            Version = version,
            Message = "Plugin host ready"
        });
    }

    /// <summary>
    /// Returns the host control capabilities exposed today.
    /// </summary>
    public override Task<CapabilitiesResponse> GetCapabilities(CapabilitiesRequest request, ServerCallContext context)
    {
        var response = new CapabilitiesResponse
        {
            Budgets = new CapabilityBudgets(),
            Permissions = new CapabilityPermissions()
        };
        response.Capabilities.AddRange(
        [
            "health",
            "capabilities"
        ]);

        ApplyManifestDefaults(response);

        return Task.FromResult(response);
    }

    private void ApplyManifestDefaults(CapabilitiesResponse response)
    {
        var records = _registry.GetSnapshot();
        if (records.Count != 1)
        {
            return;
        }

        var manifest = records[0].Manifest;
        var caps = manifest.Capabilities;
        if (caps is not null)
        {
            response.Budgets.CpuBudgetMs = caps.CpuBudgetMs;
            response.Budgets.MemoryMb = caps.MemoryMb;
        }

        var permissions = manifest.Permissions;
        if (permissions?.Domains is not null)
        {
            response.Permissions.Domains.AddRange(permissions.Domains);
        }

        if (permissions?.Paths is not null)
        {
            response.Permissions.Paths.AddRange(permissions.Paths);
        }
    }
}
