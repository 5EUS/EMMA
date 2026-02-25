using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.PluginTemplate.Services;

public sealed class PluginControlService(ILogger<PluginControlService> logger) : PluginControl.PluginControlBase
{
    private readonly ILogger<PluginControlService> _logger = logger;

    public override Task<HealthResponse> GetHealth(HealthRequest request, ServerCallContext context)
    {
        PluginRpcGuard.EnsureActive(context);
        var correlationId = PluginRpcGuard.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation("Health request {CorrelationId}", correlationId);

        var version = typeof(PluginControlService).Assembly.GetName().Version?.ToString() ?? "dev";

        return Task.FromResult(new HealthResponse
        {
            Status = "ok",
            Version = version,
            Message = "EMMA plugin template ready"
        });
    }

    public override Task<CapabilitiesResponse> GetCapabilities(CapabilitiesRequest request, ServerCallContext context)
    {
        PluginRpcGuard.EnsureActive(context);
        var correlationId = PluginRpcGuard.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation("Capabilities request {CorrelationId}", correlationId);

        var response = new CapabilitiesResponse
        {
            Budgets = new CapabilityBudgets
            {
                CpuBudgetMs = 200,
                MemoryMb = 256
            },
            Permissions = new CapabilityPermissions()
        };

        response.Capabilities.AddRange(new[]
        {
            "health",
            "capabilities",
            "search",
            "pages",
            "video"
        });

        return Task.FromResult(response);
    }
}
