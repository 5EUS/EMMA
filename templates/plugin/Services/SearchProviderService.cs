using EMMA.Contracts.Plugins;
using EMMA.Plugin.AspNetCore;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.PluginTemplate.Services;

public sealed class SearchProviderService(ILogger<SearchProviderService> logger) : SearchProvider.SearchProviderBase
{
    private readonly ILogger<SearchProviderService> _logger = logger;

    public override Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
    {
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);
        _logger.LogInformation("Search request {CorrelationId} query={Query}", correlationId, request.Query);

        return Task.FromResult(new SearchResponse());
    }
}
