using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.PluginTemplate.Services;

public sealed class SearchProviderService(ILogger<SearchProviderService> logger) : SearchProvider.SearchProviderBase
{
    private readonly ILogger<SearchProviderService> _logger = logger;

    public override Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
    {
        PluginRpcGuard.EnsureActive(context);
        var correlationId = PluginRpcGuard.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation("Search request {CorrelationId} query={Query}", correlationId, request.Query);

        var response = new SearchResponse();

        return Task.FromResult(response);
    }
}
