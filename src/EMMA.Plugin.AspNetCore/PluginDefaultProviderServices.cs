using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.Plugin.AspNetCore;

public interface IPluginPagedMediaRuntime
{
    Task<IReadOnlyList<MediaSummary>> SearchAsync(string query, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken);
    Task<MediaPage?> GetPageAsync(string chapterId, int pageIndex, CancellationToken cancellationToken);
    Task<(IReadOnlyList<MediaPage> Pages, bool ReachedEnd)> GetPagesAsync(
        string chapterId,
        int startIndex,
        int count,
        CancellationToken cancellationToken);
}

public interface IPluginVideoRuntime
{
    Task<StreamResponse> GetStreamsAsync(string mediaId, CancellationToken cancellationToken);
    Task<SegmentResponse> GetSegmentAsync(string mediaId, string streamId, int sequence, CancellationToken cancellationToken);
}

public sealed class PluginDefaultSearchProviderService<TRuntime>(
    TRuntime runtime,
    ILogger<PluginDefaultSearchProviderService<TRuntime>> logger)
    : SearchProvider.SearchProviderBase
    where TRuntime : class, IPluginPagedMediaRuntime
{
    private readonly TRuntime _runtime = runtime;
    private readonly ILogger<PluginDefaultSearchProviderService<TRuntime>> _logger = logger;

    public override async Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
    {
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Search request {CorrelationId} query={Query}",
            correlationId,
            request.Query);

        try
        {
            var response = new SearchResponse();
            var results = await _runtime.SearchAsync(request.Query ?? string.Empty, context.CancellationToken);
            response.Results.AddRange(results);
            return response;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Search request was cancelled."));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Search request {CorrelationId} failed for query={Query}.",
                correlationId,
                request.Query);

            throw new RpcException(
                new Status(
                    StatusCode.Internal,
                    $"Search request failed: {ex.GetType().Name}: {ex.Message}"));
        }
    }
}

public sealed class PluginDefaultPageProviderService<TRuntime>(
    TRuntime runtime,
    ILogger<PluginDefaultPageProviderService<TRuntime>> logger)
    : PageProvider.PageProviderBase
    where TRuntime : class, IPluginPagedMediaRuntime
{
    private readonly TRuntime _runtime = runtime;
    private readonly ILogger<PluginDefaultPageProviderService<TRuntime>> _logger = logger;

    public override async Task<ChaptersResponse> GetChapters(ChaptersRequest request, ServerCallContext context)
    {
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Chapters request {CorrelationId} mediaId={MediaId}",
            correlationId,
            request.MediaId);

        var response = new ChaptersResponse();
        var chapters = await _runtime.GetChaptersAsync(request.MediaId, context.CancellationToken);
        response.Chapters.AddRange(chapters);
        return response;
    }

    public override async Task<PageResponse> GetPage(PageRequest request, ServerCallContext context)
    {
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Page request {CorrelationId} mediaId={MediaId} chapterId={ChapterId} index={Index}",
            correlationId,
            request.MediaId,
            request.ChapterId,
            request.Index);

        var response = new PageResponse();
        var page = await _runtime.GetPageAsync(request.ChapterId, request.Index, context.CancellationToken);
        if (page is not null)
        {
            response.Page = page;
        }

        return response;
    }

    public override async Task<PagesResponse> GetPages(PagesRequest request, ServerCallContext context)
    {
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Pages request {CorrelationId} mediaId={MediaId} chapterId={ChapterId} startIndex={StartIndex} count={Count}",
            correlationId,
            request.MediaId,
            request.ChapterId,
            request.StartIndex,
            request.Count);

        var (pages, reachedEnd) = await _runtime.GetPagesAsync(
            request.ChapterId,
            request.StartIndex,
            request.Count,
            context.CancellationToken);

        var response = new PagesResponse
        {
            ReachedEnd = reachedEnd
        };
        response.Pages.AddRange(pages);
        return response;
    }
}

public sealed class PluginDefaultVideoProviderService<TRuntime>(
    TRuntime runtime,
    ILogger<PluginDefaultVideoProviderService<TRuntime>> logger)
    : VideoProvider.VideoProviderBase
    where TRuntime : class, IPluginVideoRuntime
{
    private readonly TRuntime _runtime = runtime;
    private readonly ILogger<PluginDefaultVideoProviderService<TRuntime>> _logger = logger;

    public override Task<StreamResponse> GetStreams(StreamRequest request, ServerCallContext context)
    {
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Streams request {CorrelationId} mediaId={MediaId}",
            correlationId,
            request.MediaId);

        return _runtime.GetStreamsAsync(request.MediaId, context.CancellationToken);
    }

    public override Task<SegmentResponse> GetSegment(SegmentRequest request, ServerCallContext context)
    {
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Segment request {CorrelationId} mediaId={MediaId} streamId={StreamId} sequence={Sequence}",
            correlationId,
            request.MediaId,
            request.StreamId,
            request.Sequence);

        return _runtime.GetSegmentAsync(
            request.MediaId,
            request.StreamId,
            request.Sequence,
            context.CancellationToken);
    }
}