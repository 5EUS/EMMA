using EMMA.Contracts.Plugins;
using Grpc.Core;

namespace EMMA.PluginHost.Services;

/// <summary>
/// gRPC control surface for plugin health and capability discovery.
/// </summary>
public sealed class PluginControlService : PluginControl.PluginControlBase
{
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
        var response = new CapabilitiesResponse();
        response.Capabilities.AddRange(new[]
        {
            "health",
            "capabilities"
        });

        return Task.FromResult(response);
    }
}
