using System.Collections.Concurrent;

namespace EMMA.Plugin.Common;

/// <summary>
/// Generic batch loader for metadata items with fallback to individual item fetching.
/// Reduces API calls by batching requests, with automatic fallback for any failed items.
/// </summary>
public sealed class PluginBatchMetadataLoader<TMetadata>
{
    private readonly PluginBatchMetadataLoaderOptions _options;
    private readonly ConcurrentDictionary<string, List<TMetadata>> _resultsByKey;

    /// <summary>
    /// Creates a batch metadata loader with the specified batching and delay options.
    /// </summary>
    /// <param name="options">The loader options to apply, or <see langword="null"/> to use the default options.</param>
    public PluginBatchMetadataLoader(PluginBatchMetadataLoaderOptions? options = null)
    {
        _options = options ?? PluginBatchMetadataLoaderOptions.Default;
        _resultsByKey = new ConcurrentDictionary<string, List<TMetadata>>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Loads metadata for the given keys using batch and individual fetch strategies.
    /// </summary>
    /// <param name="keys">Collection of item keys to load metadata for</param>
    /// <param name="batchFetchAsync">Async function to fetch multiple items at once; returns dict of key → metadata list</param>
    /// <param name="singleFetchAsync">Async function to fetch a single item; returns list of metadata or empty list</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A dictionary of successfully loaded metadata keyed by item identifier.</returns>
    public async Task<IReadOnlyDictionary<string, List<TMetadata>>> LoadAsync(
        IEnumerable<string> keys,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, List<TMetadata>>>> batchFetchAsync,
        Func<string, CancellationToken, Task<IReadOnlyList<TMetadata>>> singleFetchAsync,
        CancellationToken cancellationToken = default)
    {
        var keyList = keys
            .Select(k => k?.Trim() ?? string.Empty)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keyList.Count == 0)
        {
            return new Dictionary<string, List<TMetadata>>();
        }

        var results = new Dictionary<string, List<TMetadata>>(StringComparer.OrdinalIgnoreCase);
        var successfulKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Batch fetch strategy
        var batches = keyList
            .Chunk(_options.BatchSize)
            .ToList();

        foreach (var batch in batches)
        {
            try
            {
                var batchResults = await batchFetchAsync(batch, cancellationToken);
                if (batchResults?.Count > 0)
                {
                    foreach (var (key, metadata) in batchResults)
                    {
                        if (metadata?.Count > 0)
                        {
                            results[key] = [.. metadata];
                            successfulKeys.Add(key);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Batch fetch failed; will retry via individual fetch below
            }

            // Apply delay between batches if configured
            if (batch != batches.Last() && _options.DelayBetweenBatches > TimeSpan.Zero)
            {
                await Task.Delay(_options.DelayBetweenBatches, cancellationToken);
            }
        }

        // Fallback: individual fetch for any keys not successfully loaded
        foreach (var key in keyList)
        {
            if (successfulKeys.Contains(key))
            {
                continue;
            }

            try
            {
                var singleResults = await singleFetchAsync(key, cancellationToken);
                if (singleResults?.Count > 0)
                {
                    results[key] = [.. singleResults];
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Single fetch also failed; item remains unloaded
            }

            // Apply delay between single requests if configured
            if (_options.DelayBetweenRequests > TimeSpan.Zero)
            {
                await Task.Delay(_options.DelayBetweenRequests, cancellationToken);
            }
        }

        return results;
    }

    /// <summary>
    /// Synchronous wrapper around LoadAsync using GetAwaiter().GetResult().
    /// Use sparingly; prefer async version.
    /// </summary>
    /// <param name="keys">Collection of item keys to load metadata for.</param>
    /// <param name="batchFetch">Function that fetches metadata for a batch of keys.</param>
    /// <param name="singleFetch">Function that fetches metadata for a single key.</param>
    /// <returns>A dictionary of successfully loaded metadata keyed by item identifier.</returns>
    public IReadOnlyDictionary<string, List<TMetadata>> Load(
        IEnumerable<string> keys,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, List<TMetadata>>> batchFetch,
        Func<string, IReadOnlyList<TMetadata>> singleFetch)
    {
        return LoadAsync(
                keys,
                (batch, _) => Task.FromResult(batchFetch(batch)),
                (key, _) => Task.FromResult(singleFetch(key)))
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Gets the number of successfully cached results.
    /// </summary>
    /// <returns>The number of cached result entries.</returns>
    public int GetCacheSize() => _resultsByKey.Count;

    /// <summary>
    /// Clears all cached results.
    /// </summary>
    public void ClearCache()
    {
        _resultsByKey.Clear();
    }
}

/// <summary>
/// Configuration options for PluginBatchMetadataLoader.
/// </summary>
public sealed record PluginBatchMetadataLoaderOptions(
    int BatchSize,
    TimeSpan DelayBetweenBatches,
    TimeSpan DelayBetweenRequests)
{
    /// <summary>
    /// Default options: 150 items per batch, no delays.
    /// </summary>
    public static readonly PluginBatchMetadataLoaderOptions Default = new(
        BatchSize: 150,
        DelayBetweenBatches: TimeSpan.Zero,
        DelayBetweenRequests: TimeSpan.Zero);

    /// <summary>
    /// Conservative options: smaller batches with delays between requests.
    /// Use for APIs with strict rate limits.
    /// </summary>
    public static readonly PluginBatchMetadataLoaderOptions Conservative = new(
        BatchSize: 50,
        DelayBetweenBatches: new TimeSpan(0, 0, 0, 0, 500),
        DelayBetweenRequests: new TimeSpan(0, 0, 0, 0, 100));

    /// <summary>
    /// Aggressive options: large batches, minimal delays.
    /// Use for high-throughput scenarios.
    /// </summary>
    public static readonly PluginBatchMetadataLoaderOptions Aggressive = new(
        BatchSize: 300,
        DelayBetweenBatches: TimeSpan.Zero,
        DelayBetweenRequests: TimeSpan.Zero);
}
