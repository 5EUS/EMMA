using System.Collections.Concurrent;

namespace EMMA.Plugin.Common;

/// <summary>
/// Wraps an HttpClient with configurable caching and rate limiting.
/// Provides simple TTL-based caching for frequently accessed endpoints.
/// </summary>
public sealed class PluginCachedHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly PluginCachedHttpClientOptions _options;
    private readonly ConcurrentDictionary<string, CachedResponse> _cache;
    private readonly SemaphoreSlim _requestGate;
    private DateTimeOffset _lastRequestStartedUtc = DateTimeOffset.MinValue;

    private sealed record CachedResponse(string Content, DateTimeOffset FetchedAtUtc);

    /// <summary>
    /// Creates a cached HTTP client wrapper with the specified HTTP client and caching options.
    /// </summary>
    /// <param name="httpClient">The underlying HTTP client used to make requests.</param>
    /// <param name="options">The caching and rate-limiting options to apply, or <see langword="null"/> to use the default options.</param>
    public PluginCachedHttpClient(HttpClient httpClient, PluginCachedHttpClientOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? PluginCachedHttpClientOptions.Default;
        _cache = new ConcurrentDictionary<string, CachedResponse>(StringComparer.Ordinal);
        _requestGate = new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// Gets or fetches a URL, using cache if available and not expired.
    /// Applies rate limiting to respect provider API rate limits.
    /// </summary>
    /// <param name="absoluteUrl">The absolute URL to fetch.</param>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>The fetched response content, or <see langword="null"/> when the request fails or returns no content.</returns>
    public async Task<string?> GetAsync(
        string absoluteUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(absoluteUrl))
        {
            return null;
        }

        // Check cache first
        if (_cache.TryGetValue(absoluteUrl, out var cached))
        {
            var age = DateTimeOffset.UtcNow - cached.FetchedAtUtc;
            if (age < _options.CacheTtl)
            {
                return cached.Content;
            }

            // Cache expired, remove it
            _cache.TryRemove(absoluteUrl, out _);
        }

        // Apply rate limiting
        await ApplyRateLimitingAsync(cancellationToken);

        // Fetch from HTTP
        try
        {
            using var response = await _httpClient.GetAsync(absoluteUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            // Cache the response
            _cache[absoluteUrl] = new CachedResponse(content, DateTimeOffset.UtcNow);
            return content;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    /// <summary>
    /// Gets or fetches a URL with a fallback fetch function.
    /// Useful when the URL might not be immediately available.
    /// </summary>
    /// <param name="cacheKey">The cache key to use for the fetched content.</param>
    /// <param name="fetchAsync">The callback that fetches the content when the cache misses.</param>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>The fetched response content, or <see langword="null"/> when the request fails or returns no content.</returns>
    public async Task<string?> GetAsync(
        string cacheKey,
        Func<Task<string?>> fetchAsync,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return null;
        }

        // Check cache first
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            var age = DateTimeOffset.UtcNow - cached.FetchedAtUtc;
            if (age < _options.CacheTtl)
            {
                return cached.Content;
            }

            _cache.TryRemove(cacheKey, out _);
        }

        // Apply rate limiting
        await ApplyRateLimitingAsync(cancellationToken);

        // Fetch using provided function
        try
        {
            var content = await fetchAsync();
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            // Cache the response
            _cache[cacheKey] = new CachedResponse(content, DateTimeOffset.UtcNow);
            return content;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    /// <summary>
    /// Invalidates cache entries by key prefix.
    /// Useful for clearing related cache entries when data is updated.
    /// </summary>
    /// <param name="keyPrefix">The cache key prefix to invalidate.</param>
    public void InvalidateByPrefix(string keyPrefix)
    {
        if (string.IsNullOrWhiteSpace(keyPrefix))
        {
            return;
        }

        var keysToRemove = _cache.Keys
            .Where(k => k.StartsWith(keyPrefix, StringComparison.Ordinal))
            .ToList();

        foreach (var key in keysToRemove)
        {
            _cache.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Clears all cached responses.
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Gets the current cache size.
    /// </summary>
    /// <returns>The number of cached responses.</returns>
    public int GetCacheSize() => _cache.Count;

    /// <summary>
    /// Enforces minimum spacing between requests to respect rate limits.
    /// </summary>
    private async Task ApplyRateLimitingAsync(CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastRequestStartedUtc;
            if (elapsed < _options.MinRequestSpacing)
            {
                var delayMs = (int)(_options.MinRequestSpacing - elapsed).TotalMilliseconds;
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, cancellationToken);
                }
            }

            _lastRequestStartedUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _requestGate.Release();
        }
    }
}

/// <summary>
/// Configuration options for PluginCachedHttpClient.
/// </summary>
public sealed record PluginCachedHttpClientOptions(
    TimeSpan CacheTtl,
    TimeSpan MinRequestSpacing)
{
    /// <summary>
    /// Default options: 2 minute cache TTL, 250ms minimum between requests.
    /// </summary>
    public static readonly PluginCachedHttpClientOptions Default = new(
        CacheTtl: TimeSpan.FromMinutes(2),
        MinRequestSpacing: TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Conservative options: 1 hour cache, 500ms minimum between requests.
    /// Use for APIs with strict rate limits.
    /// </summary>
    public static readonly PluginCachedHttpClientOptions Conservative = new(
        CacheTtl: TimeSpan.FromHours(1),
        MinRequestSpacing: TimeSpan.FromMilliseconds(500));

    /// <summary>
    /// Aggressive options: 30 second cache, 100ms minimum between requests.
    /// Use for high-performance, low-latency scenarios.
    /// </summary>
    public static readonly PluginCachedHttpClientOptions Aggressive = new(
        CacheTtl: TimeSpan.FromSeconds(30),
        MinRequestSpacing: TimeSpan.FromMilliseconds(100));
}
