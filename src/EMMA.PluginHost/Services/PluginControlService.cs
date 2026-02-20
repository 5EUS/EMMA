using EMMA.Contracts.Plugins;
using Grpc.Core;

namespace EMMA.PluginHost.Services;

public sealed class PluginControlService : PluginControl.PluginControlBase
{
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
