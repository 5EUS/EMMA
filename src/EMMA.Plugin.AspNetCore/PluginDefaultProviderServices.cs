using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using EMMA.Plugin.Common;

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
    IPluginSdkMetrics metrics,
    ILogger<PluginDefaultSearchProviderService<TRuntime>> logger)
    : SearchProvider.SearchProviderBase
    where TRuntime : class, IPluginPagedMediaRuntime
{
    private readonly TRuntime _runtime = runtime;
    private readonly IPluginSdkMetrics _metrics = metrics;
    private readonly ILogger<PluginDefaultSearchProviderService<TRuntime>> _logger = logger;

    public override async Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
    {
        var stopwatch = Stopwatch.StartNew();
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
            _metrics.RecordRpc("search", "Search", EmmaTelemetry.Outcomes.Ok, stopwatch.Elapsed.TotalMilliseconds);
            return response;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            _metrics.RecordRpc("search", "Search", EmmaTelemetry.Outcomes.Cancelled, stopwatch.Elapsed.TotalMilliseconds);
            throw new RpcException(new Status(StatusCode.Cancelled, "Search request was cancelled."));
        }
        catch (Exception ex)
        {
            _metrics.RecordRpc("search", "Search", EmmaTelemetry.Outcomes.Error, stopwatch.Elapsed.TotalMilliseconds);
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
    IPluginSdkMetrics metrics,
    ILogger<PluginDefaultPageProviderService<TRuntime>> logger)
    : PageProvider.PageProviderBase
    where TRuntime : class, IPluginPagedMediaRuntime
{
    private readonly TRuntime _runtime = runtime;
    private readonly IPluginSdkMetrics _metrics = metrics;
    private readonly ILogger<PluginDefaultPageProviderService<TRuntime>> _logger = logger;

    public override async Task<ChaptersResponse> GetChapters(ChaptersRequest request, ServerCallContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Chapters request {CorrelationId} mediaId={MediaId}",
            correlationId,
            request.MediaId);

        try
        {
            var response = new ChaptersResponse();
            var chapters = await _runtime.GetChaptersAsync(request.MediaId, context.CancellationToken);
            response.Chapters.AddRange(chapters);
            _metrics.RecordRpc("page", "GetChapters", EmmaTelemetry.Outcomes.Ok, stopwatch.Elapsed.TotalMilliseconds);
            return response;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            _metrics.RecordRpc("page", "GetChapters", EmmaTelemetry.Outcomes.Cancelled, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
        catch
        {
            _metrics.RecordRpc("page", "GetChapters", EmmaTelemetry.Outcomes.Error, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    public override async Task<PageResponse> GetPage(PageRequest request, ServerCallContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Page request {CorrelationId} mediaId={MediaId} chapterId={ChapterId} index={Index}",
            correlationId,
            request.MediaId,
            request.ChapterId,
            request.Index);

        try
        {
            var response = new PageResponse();
            var page = await _runtime.GetPageAsync(request.ChapterId, request.Index, context.CancellationToken);
            if (page is not null)
            {
                response.Page = page;
            }

            _metrics.RecordRpc("page", "GetPage", EmmaTelemetry.Outcomes.Ok, stopwatch.Elapsed.TotalMilliseconds);
            return response;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            _metrics.RecordRpc("page", "GetPage", EmmaTelemetry.Outcomes.Cancelled, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
        catch
        {
            _metrics.RecordRpc("page", "GetPage", EmmaTelemetry.Outcomes.Error, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    public override async Task<PagesResponse> GetPages(PagesRequest request, ServerCallContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Pages request {CorrelationId} mediaId={MediaId} chapterId={ChapterId} startIndex={StartIndex} count={Count}",
            correlationId,
            request.MediaId,
            request.ChapterId,
            request.StartIndex,
            request.Count);

        try
        {
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
            _metrics.RecordRpc("page", "GetPages", EmmaTelemetry.Outcomes.Ok, stopwatch.Elapsed.TotalMilliseconds);
            return response;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            _metrics.RecordRpc("page", "GetPages", EmmaTelemetry.Outcomes.Cancelled, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
        catch
        {
            _metrics.RecordRpc("page", "GetPages", EmmaTelemetry.Outcomes.Error, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }
}

public sealed class PluginDefaultVideoProviderService<TRuntime>(
    TRuntime runtime,
    IPluginSdkMetrics metrics,
    ILogger<PluginDefaultVideoProviderService<TRuntime>> logger)
    : VideoProvider.VideoProviderBase
    where TRuntime : class, IPluginVideoRuntime
{
    private readonly TRuntime _runtime = runtime;
    private readonly IPluginSdkMetrics _metrics = metrics;
    private readonly ILogger<PluginDefaultVideoProviderService<TRuntime>> _logger = logger;

    public override Task<StreamResponse> GetStreams(StreamRequest request, ServerCallContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Streams request {CorrelationId} mediaId={MediaId}",
            correlationId,
            request.MediaId);

        return ExecuteWithMetrics(
            "GetStreams",
            stopwatch,
            context.CancellationToken,
            () => _runtime.GetStreamsAsync(request.MediaId, context.CancellationToken));
    }

    public override Task<SegmentResponse> GetSegment(SegmentRequest request, ServerCallContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Segment request {CorrelationId} mediaId={MediaId} streamId={StreamId} sequence={Sequence}",
            correlationId,
            request.MediaId,
            request.StreamId,
            request.Sequence);

        return ExecuteWithMetrics(
            "GetSegment",
            stopwatch,
            context.CancellationToken,
            () => _runtime.GetSegmentAsync(
                request.MediaId,
                request.StreamId,
                request.Sequence,
                context.CancellationToken));
    }

    private async Task<T> ExecuteWithMetrics<T>(
        string method,
        Stopwatch stopwatch,
        CancellationToken cancellationToken,
        Func<Task<T>> action)
    {
        try
        {
            var response = await action();
            _metrics.RecordRpc("video", method, EmmaTelemetry.Outcomes.Ok, stopwatch.Elapsed.TotalMilliseconds);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _metrics.RecordRpc("video", method, EmmaTelemetry.Outcomes.Cancelled, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
        catch
        {
            _metrics.RecordRpc("video", method, EmmaTelemetry.Outcomes.Error, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }
}