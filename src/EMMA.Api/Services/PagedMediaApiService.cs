using EMMA.Application.Pipelines;
using EMMA.Contracts.Api.V1;
using EMMA.Domain;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.Api.Services;

public sealed class PagedMediaApiService(EmbeddedRuntime runtime, ILogger<PagedMediaApiService> logger)
    : PagedMediaApi.PagedMediaApiBase
{
    private readonly EmbeddedRuntime _runtime = runtime;
    private readonly ILogger<PagedMediaApiService> _logger = logger;

    public override async Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
    {
        try
        {
            var results = await _runtime.Pipeline.SearchAsync(request.Query ?? string.Empty, context.CancellationToken);
            var response = new SearchResponse
            {
                Result = new SearchResult()
            };
            response.Result.Items.AddRange(results.Select(MapSummary));
            return response;
        }
        catch (Exception ex)
        {
            return new SearchResponse { Error = CreateError(ex) };
        }
    }

    public override async Task<ChaptersResponse> GetChapters(ChaptersRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.MediaId))
        {
            return new ChaptersResponse { Error = InvalidRequest("media_id is required.") };
        }

        try
        {
            var chapters = await _runtime.Pipeline.GetChaptersAsync(
                MediaId.Create(request.MediaId),
                context.CancellationToken);

            var response = new ChaptersResponse
            {
                Result = new ChaptersResult()
            };
            response.Result.Items.AddRange(chapters.Select(MapChapter));
            return response;
        }
        catch (Exception ex)
        {
            return new ChaptersResponse { Error = CreateError(ex) };
        }
    }

    public override async Task<PageResponse> GetPage(PageRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.MediaId) || string.IsNullOrWhiteSpace(request.ChapterId))
        {
            return new PageResponse { Error = InvalidRequest("media_id and chapter_id are required.") };
        }

        if (request.Index < 0)
        {
            return new PageResponse { Error = InvalidRequest("index must be >= 0.") };
        }

        try
        {
            var page = await _runtime.Pipeline.GetPageAsync(
                MediaId.Create(request.MediaId),
                request.ChapterId,
                request.Index,
                context.CancellationToken);

            return new PageResponse { Page = MapPage(page) };
        }
        catch (Exception ex)
        {
            return new PageResponse { Error = CreateError(ex) };
        }
    }

    public override async Task<PageAssetResponse> GetPageAsset(PageAssetRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.MediaId) || string.IsNullOrWhiteSpace(request.ChapterId))
        {
            return new PageAssetResponse { Error = InvalidRequest("media_id and chapter_id are required.") };
        }

        if (request.Index < 0)
        {
            return new PageAssetResponse { Error = InvalidRequest("index must be >= 0.") };
        }

        try
        {
            var page = await _runtime.Pipeline.GetPageAsync(
                MediaId.Create(request.MediaId),
                request.ChapterId,
                request.Index,
                context.CancellationToken);

            var asset = await _runtime.Pipeline.GetPageAssetAsync(page, context.CancellationToken);
            return new PageAssetResponse
            {
                Asset = new ApiPageAsset
                {
                    ContentType = asset.ContentType,
                    Payload = Google.Protobuf.ByteString.CopyFrom(asset.Payload)
                }
            };
        }
        catch (Exception ex)
        {
            return new PageAssetResponse { Error = CreateError(ex) };
        }
    }

    private ApiMediaSummary MapSummary(MediaSummary summary)
    {
        return new ApiMediaSummary
        {
            Id = summary.Id.Value,
            Source = summary.SourceId,
            Title = summary.Title,
            MediaType = MapMediaType(summary.MediaType)
        };
    }

    private ApiMediaChapter MapChapter(MediaChapter chapter)
    {
        return new ApiMediaChapter
        {
            Id = chapter.ChapterId,
            Number = chapter.Number,
            Title = chapter.Title
        };
    }

    private ApiMediaPage MapPage(MediaPage page)
    {
        return new ApiMediaPage
        {
            Id = page.PageId,
            Index = page.Index,
            ContentUri = page.ContentUri.ToString()
        };
    }

    private static ApiMediaType MapMediaType(MediaType mediaType)
    {
        return mediaType switch
        {
            MediaType.Video => ApiMediaType.Video,
            MediaType.Paged => ApiMediaType.Paged,
            _ => ApiMediaType.Unspecified
        };
    }

    private ApiError CreateError(Exception ex)
    {
        var error = ex switch
        {
            KeyNotFoundException => new ApiError { Code = "not_found" },
            TimeoutException => new ApiError { Code = "timeout" },
            OperationCanceledException => new ApiError { Code = "cancelled" },
            InvalidOperationException => new ApiError { Code = "invalid_request" },
            _ => new ApiError { Code = "upstream_failure" }
        };

        error.Message = string.IsNullOrWhiteSpace(ex.Message) ? "Request failed." : ex.Message;
        return error;
    }

    private ApiError InvalidRequest(string message)
    {
        return new ApiError
        {
            Code = "invalid_request",
            Message = message
        };
    }
}
