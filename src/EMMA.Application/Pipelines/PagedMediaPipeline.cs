using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// A pipeline that orchestrates paged media-related operation.
/// </summary>
/// <param name="search"></param>
/// <param name="pages"></param>
/// <param name="policy"></param>
/// <param name="cache"></param>
public sealed class PagedMediaPipeline(
    IMediaSearchPort search,
    IPageProviderPort pages,
    IPolicyEvaluator policy,
    ICachePort cache,
    PagedMediaPipelineOptions? options = null,
    IPageAssetCachePort? pageAssetCache = null,
    IPageAssetFetcherPort? pageAssetFetcher = null,
    IMediaCatalogPort? catalog = null)
{
    private readonly IMediaSearchPort _search = search;
    private readonly IPageProviderPort _pages = pages;
    private readonly IPolicyEvaluator _policy = policy;
    private readonly ICachePort _cache = cache;
    private readonly PagedMediaPipelineOptions _options = options ?? PagedMediaPipelineOptions.Default;
    private readonly IPageAssetCachePort? _pageAssetCache = pageAssetCache;
    private readonly IPageAssetFetcherPort? _pageAssetFetcher = pageAssetFetcher;
    private readonly IMediaCatalogPort? _catalog = catalog;

    /// <summary>
    /// Searches for media summaries matching the given query. Results are cached for 5 minutes.
    /// </summary>
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IReadOnlyList<MediaSummary>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        EnsureNetworkAllowed("search");

        var cacheKey = $"search:{query.Trim()}";
        var cached = await _cache.GetAsync<IReadOnlyList<MediaSummary>>(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var results = await _search.SearchAsync(query.Trim(), cancellationToken);
        await PersistSearchResultsAsync(results, cancellationToken);
        await _cache.SetAsync(cacheKey, results, TimeSpan.FromMinutes(5), cancellationToken);

        return results;
    }

    /// <summary>
    /// Gets the chapters for a given media. Results are cached for 10 minutes.
    /// </summary>
    /// <param name="mediaId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(MediaId mediaId, CancellationToken cancellationToken)
    {
        EnsureNetworkAllowed("chapters");

        var cacheKey = $"chapters:{mediaId}";
        var cached = await _cache.GetAsync<IReadOnlyList<MediaChapter>>(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var chapters = await _pages.GetChaptersAsync(mediaId, cancellationToken);
        await PersistChaptersAsync(mediaId, chapters, cancellationToken);
        await _cache.SetAsync(cacheKey, chapters, TimeSpan.FromMinutes(10), cancellationToken);

        return chapters;
    }

    /// <summary>
    /// Gets a specific page for a given media and chapter. No caching is applied to pages due to their potentially large size.
    /// </summary>
    /// <param name="mediaId"></param>
    /// <param name="chapterId"></param>
    /// <param name="pageIndex"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<MediaPage> GetPageAsync(
        MediaId mediaId,
        string chapterId,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        EnsureNetworkAllowed("page");
        var page = await GetPageWithRetryAsync(mediaId, chapterId, pageIndex, cancellationToken);
        await PersistPageAsync(mediaId, chapterId, page, cancellationToken);
        return page;
    }

    /// <summary>
    /// Gets a batch of pages for a given media and chapter.
    /// </summary>
    public async Task<MediaPagesResult> GetPagesAsync(
        MediaId mediaId,
        string chapterId,
        int startIndex,
        int count,
        CancellationToken cancellationToken)
    {
        EnsureNetworkAllowed("page");
        var result = await _pages.GetPagesAsync(mediaId, chapterId, startIndex, count, cancellationToken);
        foreach (var page in result.Pages)
        {
            await PersistPageAsync(mediaId, chapterId, page, cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Fetches page assets using the configured cache and fetcher ports.
    /// </summary>
    /// <param name="page"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task<MediaPageAsset> GetPageAssetAsync(MediaPage page, CancellationToken cancellationToken)
    {
        if (_pageAssetCache is null || _pageAssetFetcher is null)
        {
            throw new InvalidOperationException("Page asset cache or fetcher is not configured.");
        }

        EnsureNetworkAllowed("page-asset");
        EnsureCacheAllowed("page-asset");

        var cacheKey = $"page-asset:{page.ContentUri}";
        var cached = await _pageAssetCache.GetAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var asset = await _pageAssetFetcher.FetchAsync(page.ContentUri, cancellationToken);
        await _pageAssetCache.SetAsync(cacheKey, asset, cancellationToken);
        return asset;
    }

    private async Task<MediaPage> GetPageWithRetryAsync(
        MediaId mediaId,
        string chapterId,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var retryCount = Math.Max(0, _options.PageRetryCount);
        var delay = _options.PageRetryDelay;
        for (var attempt = 0; ; attempt++)
        {
            using var cts = CreatePageTimeout(cancellationToken);

            Exception? lastError;
            try
            {
                return await _pages.GetPageAsync(mediaId, chapterId, pageIndex, cts.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                lastError = new TimeoutException("Page fetch timed out.", ex);
            }
            catch (TimeoutException ex)
            {
                lastError = ex;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            if (attempt >= retryCount)
            {
                throw lastError ?? new InvalidOperationException("Page fetch failed after retries.");
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private CancellationTokenSource CreatePageTimeout(CancellationToken cancellationToken)
    {
        if (_options.PageTimeout <= TimeSpan.Zero)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.PageTimeout);
        return cts;
    }

    /// <summary>
    /// Ensures that the current policy allows network access for the specified target. Throws an exception if access is denied.
    /// </summary>
    /// <param name="target"></param>
    /// <exception cref="InvalidOperationException"></exception>
    private void EnsureNetworkAllowed(string target)
    {
        var decision = _policy.Evaluate(new CapabilityRequest(CapabilityKind.Network, target));
        if (!decision.Allowed)
        {
            throw new InvalidOperationException(decision.Reason ?? "Network access denied.");
        }
    }

    private void EnsureCacheAllowed(string target)
    {
        var decision = _policy.Evaluate(new CapabilityRequest(CapabilityKind.Cache, target));
        if (!decision.Allowed)
        {
            throw new InvalidOperationException(decision.Reason ?? "Cache access denied.");
        }
    }

    private async Task PersistSearchResultsAsync(
        IReadOnlyList<MediaSummary> results,
        CancellationToken cancellationToken)
    {
        if (_catalog is null || results.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var item in results)
        {
            var metadata = new MediaMetadata(
                item.Id,
                item.SourceId,
                item.Title,
                item.MediaType,
                null,
                item.Description,
                null,
                [],
                now,
                now);

            await _catalog.UpsertMediaAsync(metadata, cancellationToken);
        }
    }

    private async Task PersistChaptersAsync(
        MediaId mediaId,
        IReadOnlyList<MediaChapter> chapters,
        CancellationToken cancellationToken)
    {
        if (_catalog is null || !await MediaExistsAsync(mediaId, cancellationToken))
        {
            return;
        }

        var records = chapters.Select(chapter => new MediaChapterRecord(
            chapter.ChapterId,
            mediaId,
            chapter.Number,
            chapter.Title,
            null)).ToList();

        await _catalog.UpsertChaptersAsync(mediaId, records, cancellationToken);
    }

    private async Task PersistPageAsync(
        MediaId mediaId,
        string chapterId,
        MediaPage page,
        CancellationToken cancellationToken)
    {
        if (_catalog is null || !await MediaExistsAsync(mediaId, cancellationToken))
        {
            return;
        }

        IReadOnlyList<MediaPageRecord> records =
        [
            new MediaPageRecord(
                    page.PageId,
                    mediaId,
                    chapterId,
                    page.Index,
                    page.ContentUri.ToString(),
                    DateTimeOffset.UtcNow)
        ];

        await _catalog.UpsertPagesAsync(mediaId, chapterId, records, cancellationToken);
    }

    private async Task<bool> MediaExistsAsync(MediaId mediaId, CancellationToken cancellationToken)
    {
        if (_catalog is null)
        {
            return false;
        }

        var existing = await _catalog.GetMediaAsync(mediaId, cancellationToken);
        return existing is not null;
    }
}
