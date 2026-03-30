using EMMA.Contracts.Api.V1;
using EMMA.Domain;

namespace EMMA.Api.Embedded;

public sealed class EmbeddedPagedMediaApi(EmbeddedRuntime runtime)
{
    private readonly EmbeddedRuntime _runtime = runtime;

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        return await ExecuteSafelyAsync(async () =>
        {
            var results = await _runtime.Pipeline.SearchAsync(
                request.Query ?? string.Empty,
                cancellationToken);

            var response = new SearchResponse
            {
                Result = new SearchResult()
            };
            response.Result.Items.AddRange(results.Select(Services.PagedMediaApiMapper.MapSummary));
            return response;
        }, error => new SearchResponse { Error = error });
    }

    public async Task<ChaptersResponse> GetChaptersAsync(ChaptersRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.MediaId))
        {
            return new ChaptersResponse { Error = Services.PagedMediaApiMapper.InvalidRequest("media_id is required.") };
        }

        return await ExecuteSafelyAsync(async () =>
        {
            var chapters = await _runtime.Pipeline.GetChaptersAsync(
                MediaId.Create(request.MediaId),
                cancellationToken);

            var response = new ChaptersResponse
            {
                Result = new ChaptersResult()
            };
            response.Result.Items.AddRange(chapters.Select(Services.PagedMediaApiMapper.MapChapter));
            return response;
        }, error => new ChaptersResponse { Error = error });
    }

    public async Task<PageResponse> GetPageAsync(PageRequest request, CancellationToken cancellationToken)
    {
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
                cancellationToken);

            return new PageResponse { Page = Services.PagedMediaApiMapper.MapPage(page) };
        }, error => new PageResponse { Error = error });
    }

    public async Task<PageAssetResponse> GetPageAssetAsync(PageAssetRequest request, CancellationToken cancellationToken)
    {
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
                cancellationToken);

            var asset = await _runtime.Pipeline.GetPageAssetAsync(page, cancellationToken);
            return new PageAssetResponse
            {
                Asset = Services.PagedMediaApiMapper.MapAsset(asset)
            };
        }, error => new PageAssetResponse { Error = error });
    }

    private static ApiError? ValidatePageRequest(string mediaId, string chapterId, int index)
    {
        if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
        {
            return Services.PagedMediaApiMapper.InvalidRequest("media_id and chapter_id are required.");
        }

        if (index < 0)
        {
            return Services.PagedMediaApiMapper.InvalidRequest("index must be >= 0.");
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
            return mapError(Services.PagedMediaApiMapper.CreateError(ex));
        }
    }
}
