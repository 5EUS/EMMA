using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using EMMA.Plugin.Common;

namespace EMMA.Plugin.AspNetCore;

/// <summary>
/// Defines the paged-media runtime operations required by the default gRPC services.
/// </summary>
public interface IPluginPagedMediaRuntime
{
    /// <summary>
    /// Searches the upstream source for media that matches the provided query.
    /// </summary>
    /// <param name="query">The user query to search for.</param>
    /// <param name="cancellationToken">Cancels the in-flight runtime operation.</param>
    /// <returns>A list of matching media summaries.</returns>
    Task<IReadOnlyList<MediaSummary>> SearchAsync(string query, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the chapter list for a specific media item.
    /// </summary>
    /// <param name="mediaId">The media identifier to resolve chapters for.</param>
    /// <param name="cancellationToken">Cancels the in-flight runtime operation.</param>
    /// <returns>The chapters exposed by the runtime for the media item.</returns>
    Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken);

    /// <summary>
    /// Loads a single page from a chapter.
    /// </summary>
    /// <param name="chapterId">The chapter identifier that owns the page.</param>
    /// <param name="pageIndex">The zero-based page index to load.</param>
    /// <param name="cancellationToken">Cancels the in-flight runtime operation.</param>
    /// <returns>The page when available; otherwise <see langword="null"/>.</returns>
    Task<MediaPage?> GetPageAsync(string chapterId, int pageIndex, CancellationToken cancellationToken);

    /// <summary>
    /// Loads a range of pages from a chapter.
    /// </summary>
    /// <param name="chapterId">The chapter identifier that owns the requested pages.</param>
    /// <param name="startIndex">The zero-based page index to start from.</param>
    /// <param name="count">The maximum number of pages to load.</param>
    /// <param name="cancellationToken">Cancels the in-flight runtime operation.</param>
    /// <returns>A page batch and a flag indicating whether the chapter was exhausted.</returns>
    Task<(IReadOnlyList<MediaPage> Pages, bool ReachedEnd)> GetPagesAsync(
        string chapterId,
        int startIndex,
        int count,
        CancellationToken cancellationToken);
}

/// <summary>
/// Defines search-result metadata enrichment operations.
/// </summary>
public interface IPluginSearchMetadataRuntime
{
    /// <summary>
    /// Enriches search results with additional metadata before they are returned to the host.
    /// </summary>
    /// <param name="items">The search items to enrich.</param>
    /// <param name="cancellationToken">Cancels the in-flight runtime operation.</param>
    /// <returns>The enriched search items.</returns>
    Task<IReadOnlyList<SearchItem>> EnrichSearchItemsAsync(
        IReadOnlyList<SearchItem> items,
        CancellationToken cancellationToken);
}

/// <summary>
/// Defines the video runtime operations required by the default gRPC services.
/// </summary>
public interface IPluginVideoRuntime
{
    /// <summary>
    /// Loads the playable streams for a video media item.
    /// </summary>
    /// <param name="mediaId">The media identifier to resolve streams for.</param>
    /// <param name="cancellationToken">Cancels the in-flight runtime operation.</param>
    /// <returns>The stream response returned by the runtime.</returns>
    Task<StreamResponse> GetStreamsAsync(string mediaId, CancellationToken cancellationToken);

    /// <summary>
    /// Loads a single stream segment for a video media item.
    /// </summary>
    /// <param name="mediaId">The media identifier that owns the segment.</param>
    /// <param name="streamId">The stream identifier that owns the segment.</param>
    /// <param name="sequence">The segment sequence number to fetch.</param>
    /// <param name="cancellationToken">Cancels the in-flight runtime operation.</param>
    /// <returns>The segment response returned by the runtime.</returns>
    Task<SegmentResponse> GetSegmentAsync(string mediaId, string streamId, int sequence, CancellationToken cancellationToken);
}

/// <summary>
/// Implements the default search gRPC service by delegating requests to a paged-media runtime.
/// </summary>
/// <param name="runtime">The runtime that executes search operations.</param>
/// <param name="metrics">The metrics recorder used for RPC instrumentation.</param>
/// <param name="logger">The logger used for request diagnostics.</param>
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

    /// <summary>
    /// Handles a gRPC search request by delegating to the runtime and recording metrics.
    /// </summary>
    /// <param name="request">The incoming search request.</param>
    /// <param name="context">The active gRPC server call context.</param>
    /// <returns>A search response containing any matching results.</returns>
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

    /// <summary>
    /// Handles a gRPC search-enrichment request by delegating to the runtime when supported.
    /// </summary>
    /// <param name="request">The incoming enrichment request.</param>
    /// <param name="context">The active gRPC server call context.</param>
    /// <returns>A response containing enriched search items.</returns>
    public override async Task<EnrichSearchItemsResponse> EnrichSearchItems(EnrichSearchItemsRequest request, ServerCallContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);

        try
        {
            var response = new EnrichSearchItemsResponse();
            var items = request.Items.Select(MapContractSearchItem).ToArray();

            IReadOnlyList<SearchItem> enriched = items;
            if (_runtime is IPluginSearchMetadataRuntime metadataRuntime)
            {
                enriched = await metadataRuntime.EnrichSearchItemsAsync(items, context.CancellationToken);
            }

            response.Results.AddRange(enriched.Select(MapRuntimeSearchItem));
            _metrics.RecordRpc("search", "EnrichSearchItems", EmmaTelemetry.Outcomes.Ok, stopwatch.Elapsed.TotalMilliseconds);
            return response;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            _metrics.RecordRpc("search", "EnrichSearchItems", EmmaTelemetry.Outcomes.Cancelled, stopwatch.Elapsed.TotalMilliseconds);
            throw new RpcException(new Status(StatusCode.Cancelled, "Search enrichment request was cancelled."));
        }
        catch (Exception ex)
        {
            _metrics.RecordRpc("search", "EnrichSearchItems", EmmaTelemetry.Outcomes.Error, stopwatch.Elapsed.TotalMilliseconds);
            _logger.LogError(ex, "Search enrichment request {CorrelationId} failed.", correlationId);
            throw new RpcException(
                new Status(
                    StatusCode.Internal,
                    $"Search enrichment failed: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    private static SearchItem MapContractSearchItem(MediaSummary item)
    {
        var metadata = item.Metadata.Count == 0
            ? null
            : item.Metadata.Select(static entry => new MetadataItem(entry.Key, entry.Value)).ToArray();

        return new SearchItem(
            item.Id,
            item.Source,
            item.Title,
            item.MediaType,
            string.IsNullOrWhiteSpace(item.ThumbnailUrl) ? null : item.ThumbnailUrl,
            string.IsNullOrWhiteSpace(item.Description) ? null : item.Description,
            metadata);
    }

    private static MediaSummary MapRuntimeSearchItem(SearchItem item)
    {
        var metadata = item.metadata?.Count > 0
            ? item.metadata.Select(static entry => new KeyValue { Key = entry.key, Value = entry.value }).ToArray()
            : [];

        var summary = new MediaSummary
        {
            Id = item.id,
            Source = item.source,
            Title = item.title,
            MediaType = item.mediaType,
            ThumbnailUrl = item.thumbnailUrl ?? string.Empty,
            Description = item.description ?? string.Empty
        };
        summary.Metadata.AddRange(metadata);
        return summary;
    }
}

/// <summary>
/// Implements the default page gRPC service by delegating requests to a paged-media runtime.
/// </summary>
/// <param name="runtime">The runtime that executes page operations.</param>
/// <param name="metrics">The metrics recorder used for RPC instrumentation.</param>
/// <param name="logger">The logger used for request diagnostics.</param>
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

    /// <summary>
    /// Handles a gRPC chapter request by delegating to the paged runtime and recording metrics.
    /// </summary>
    /// <param name="request">The incoming chapters request.</param>
    /// <param name="context">The active gRPC server call context.</param>
    /// <returns>A response containing the chapter list for the requested media item.</returns>
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

    /// <summary>
    /// Handles a gRPC single-page request by delegating to the paged runtime and recording metrics.
    /// </summary>
    /// <param name="request">The incoming page request.</param>
    /// <param name="context">The active gRPC server call context.</param>
    /// <returns>A response containing the requested page when one is available.</returns>
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

    /// <summary>
    /// Handles a gRPC paged batch request by delegating to the paged runtime and recording metrics.
    /// </summary>
    /// <param name="request">The incoming pages request.</param>
    /// <param name="context">The active gRPC server call context.</param>
    /// <returns>A response containing the requested page range.</returns>
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

/// <summary>
/// Implements the default video gRPC service by delegating requests to a video runtime.
/// </summary>
/// <param name="runtime">The runtime that executes video operations.</param>
/// <param name="metrics">The metrics recorder used for RPC instrumentation.</param>
/// <param name="logger">The logger used for request diagnostics.</param>
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

    /// <summary>
    /// Handles a gRPC video streams request by delegating to the runtime and recording metrics.
    /// </summary>
    /// <param name="request">The incoming stream request.</param>
    /// <param name="context">The active gRPC server call context.</param>
    /// <returns>A response containing the available streams for the requested media item.</returns>
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

            /// <summary>
            /// Handles a gRPC video segment request by delegating to the runtime and recording metrics.
            /// </summary>
            /// <param name="request">The incoming segment request.</param>
            /// <param name="context">The active gRPC server call context.</param>
            /// <returns>A response containing the requested stream segment.</returns>
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