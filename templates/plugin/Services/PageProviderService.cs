using EMMA.Contracts.Plugins;
using EMMA.Plugin.AspNetCore;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.PluginTemplate.Services;

public sealed class PageProviderService(ILogger<PageProviderService> logger) : PageProvider.PageProviderBase
{
    private readonly ILogger<PageProviderService> _logger = logger;

    public override Task<ChaptersResponse> GetChapters(ChaptersRequest request, ServerCallContext context)
    {
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);
        _logger.LogInformation("Chapters request {CorrelationId} mediaId={MediaId}", correlationId, request.MediaId);

        return Task.FromResult(new ChaptersResponse());
    }

    public override Task<PageResponse> GetPage(PageRequest request, ServerCallContext context)
    {
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);
        _logger.LogInformation(
            "Page request {CorrelationId} mediaId={MediaId} chapterId={ChapterId} index={Index}",
            correlationId,
            request.MediaId,
            request.ChapterId,
            request.Index);

        return Task.FromResult(new PageResponse());
    }
}
