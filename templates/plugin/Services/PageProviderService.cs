using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.PluginTemplate.Services;

public sealed class PageProviderService(ILogger<PageProviderService> logger) : PageProvider.PageProviderBase
{
    private readonly ILogger<PageProviderService> _logger = logger;

    public override Task<ChaptersResponse> GetChapters(ChaptersRequest request, ServerCallContext context)
    {
        PluginRpcGuard.EnsureActive(context);
        var correlationId = PluginRpcGuard.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation("Chapters request {CorrelationId} mediaId={MediaId}", correlationId, request.MediaId);

        var response = new ChaptersResponse();

        return Task.FromResult(response);
    }

    public override Task<PageResponse> GetPage(PageRequest request, ServerCallContext context)
    {
        PluginRpcGuard.EnsureActive(context);
        var correlationId = PluginRpcGuard.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Page request {CorrelationId} mediaId={MediaId} chapterId={ChapterId} index={Index}",
            correlationId,
            request.MediaId,
            request.ChapterId,
            request.Index);

        var response = new PageResponse();

        return Task.FromResult(response);
    }
}
