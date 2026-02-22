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
    PagedMediaPipelineOptions? options = null)
{
    private readonly IMediaSearchPort _search = search;
    private readonly IPageProviderPort _pages = pages;
    private readonly IPolicyEvaluator _policy = policy;
    private readonly ICachePort _cache = cache;
    private readonly PagedMediaPipelineOptions _options = options ?? PagedMediaPipelineOptions.Default;

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
    public Task<MediaPage> GetPageAsync(
        MediaId mediaId,
        string chapterId,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        EnsureNetworkAllowed("page");
        return GetPageWithRetryAsync(mediaId, chapterId, pageIndex, cancellationToken);
    }

    private async Task<MediaPage> GetPageWithRetryAsync(
        MediaId mediaId,
        string chapterId,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var retryCount = Math.Max(0, _options.PageRetryCount);
        var delay = _options.PageRetryDelay;

        Exception? lastError = null;

        for (var attempt = 0; ; attempt++)
        {
            using var cts = CreatePageTimeout(cancellationToken);

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
}
