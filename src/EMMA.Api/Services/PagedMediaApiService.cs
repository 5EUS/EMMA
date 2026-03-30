using EMMA.Application.Pipelines;
using EMMA.Contracts.Api.V1;
using EMMA.Domain;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.Api.Services;

public sealed class PagedMediaApiService(
    EmbeddedRuntime runtime,
    IClientIdentityAccessor identityAccessor,
    ILogger<PagedMediaApiService> logger)
    : PagedMediaApi.PagedMediaApiBase
{
    private readonly EmbeddedRuntime _runtime = runtime;
    private readonly IClientIdentityAccessor _identityAccessor = identityAccessor;
    private readonly ILogger<PagedMediaApiService> _logger = logger;

    public override async Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
    {
        using var scope = BeginClientScope();
        return await ExecuteSafelyAsync(async () =>
        {
            var results = await _runtime.Pipeline.SearchAsync(request.Query ?? string.Empty, context.CancellationToken);
            var response = new SearchResponse
            {
                Result = new SearchResult()
            };
            response.Result.Items.AddRange(results.Select(PagedMediaApiMapper.MapSummary));
            return response;
        }, error => new SearchResponse { Error = error });
    }

    public override async Task<ChaptersResponse> GetChapters(ChaptersRequest request, ServerCallContext context)
    {
        using var scope = BeginClientScope();
        if (string.IsNullOrWhiteSpace(request.MediaId))
        {
            return new ChaptersResponse { Error = PagedMediaApiMapper.InvalidRequest("media_id is required.") };
        }

        return await ExecuteSafelyAsync(async () =>
        {
            var chapters = await _runtime.Pipeline.GetChaptersAsync(
                MediaId.Create(request.MediaId),
                context.CancellationToken);

            var response = new ChaptersResponse
            {
                Result = new ChaptersResult()
            };
            response.Result.Items.AddRange(chapters.Select(PagedMediaApiMapper.MapChapter));
            return response;
        }, error => new ChaptersResponse { Error = error });
    }

    public override async Task<PageResponse> GetPage(PageRequest request, ServerCallContext context)
    {
        using var scope = BeginClientScope();
        var validationError = ValidatePageRequest(request.MediaId, request.ChapterId, request.Index);
        if (validationError is not null)
        {
            return new PageResponse { Error = validationError };
        }

        return await ExecuteSafelyAsync(async () =>
        {
            var page = await _runtime.Pipeline.GetPageAsync(
                MediaId.Create(request.MediaId),
                request.ChapterId,
                request.Index,
                context.CancellationToken);

            return new PageResponse { Page = PagedMediaApiMapper.MapPage(page) };
        }, error => new PageResponse { Error = error });
    }

    public override async Task<PageAssetResponse> GetPageAsset(PageAssetRequest request, ServerCallContext context)
    {
        using var scope = BeginClientScope();
        var validationError = ValidatePageRequest(request.MediaId, request.ChapterId, request.Index);
        if (validationError is not null)
        {
            return new PageAssetResponse { Error = validationError };
        }

        return await ExecuteSafelyAsync(async () =>
        {
            var page = await _runtime.Pipeline.GetPageAsync(
                MediaId.Create(request.MediaId),
                request.ChapterId,
                request.Index,
                context.CancellationToken);

            var asset = await _runtime.Pipeline.GetPageAssetAsync(page, context.CancellationToken);
            return new PageAssetResponse
            {
                Asset = PagedMediaApiMapper.MapAsset(asset)
            };
        }, error => new PageAssetResponse { Error = error });
    }

    private static ApiError? ValidatePageRequest(string mediaId, string chapterId, int index)
    {
        if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
        {
            return PagedMediaApiMapper.InvalidRequest("media_id and chapter_id are required.");
        }

        if (index < 0)
        {
            return PagedMediaApiMapper.InvalidRequest("index must be >= 0.");
        }

        return null;
    }

    private static async Task<TResponse> ExecuteSafelyAsync<TResponse>(
        Func<Task<TResponse>> action,
        Func<ApiError, TResponse> mapError)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            return mapError(PagedMediaApiMapper.CreateError(ex));
        }
    }

    private IDisposable? BeginClientScope()
    {
        var clientId = _identityAccessor.Current?.ClientId ?? "anonymous";
        return _logger.BeginScope(new Dictionary<string, object>
        {
            ["ClientId"] = clientId
        });
    }
}
