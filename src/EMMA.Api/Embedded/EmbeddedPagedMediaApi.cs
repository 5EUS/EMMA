using EMMA.Contracts.Api.V1;
using EMMA.Domain;

namespace EMMA.Api.Embedded;

public sealed class EmbeddedPagedMediaApi(EmbeddedRuntime runtime)
{
    private readonly EmbeddedRuntime _runtime = runtime;

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        try
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
        }
        catch (Exception ex)
        {
            return new SearchResponse { Error = Services.PagedMediaApiMapper.CreateError(ex) };
        }
    }

    public async Task<ChaptersResponse> GetChaptersAsync(ChaptersRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.MediaId))
        {
            return new ChaptersResponse { Error = Services.PagedMediaApiMapper.InvalidRequest("media_id is required.") };
        }

        try
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
        }
        catch (Exception ex)
        {
            return new ChaptersResponse { Error = Services.PagedMediaApiMapper.CreateError(ex) };
        }
    }

    public async Task<PageResponse> GetPageAsync(PageRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.MediaId) || string.IsNullOrWhiteSpace(request.ChapterId))
        {
            return new PageResponse { Error = Services.PagedMediaApiMapper.InvalidRequest("media_id and chapter_id are required.") };
        }

        if (request.Index < 0)
        {
            return new PageResponse { Error = Services.PagedMediaApiMapper.InvalidRequest("index must be >= 0.") };
        }

        try
        {
            var page = await _runtime.Pipeline.GetPageAsync(
                MediaId.Create(request.MediaId),
                request.ChapterId,
                request.Index,
                cancellationToken);

            return new PageResponse { Page = Services.PagedMediaApiMapper.MapPage(page) };
        }
        catch (Exception ex)
        {
            return new PageResponse { Error = Services.PagedMediaApiMapper.CreateError(ex) };
        }
    }

    public async Task<PageAssetResponse> GetPageAssetAsync(PageAssetRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.MediaId) || string.IsNullOrWhiteSpace(request.ChapterId))
        {
            return new PageAssetResponse { Error = Services.PagedMediaApiMapper.InvalidRequest("media_id and chapter_id are required.") };
        }

        if (request.Index < 0)
        {
            return new PageAssetResponse { Error = Services.PagedMediaApiMapper.InvalidRequest("index must be >= 0.") };
        }

        try
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
        }
        catch (Exception ex)
        {
            return new PageAssetResponse { Error = Services.PagedMediaApiMapper.CreateError(ex) };
        }
    }
}
