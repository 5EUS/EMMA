using System.Collections.Concurrent;
using System.Text.Json;

namespace EMMA.Plugin.Common;

/// <summary>
/// Generic base class for provider-specific search query enrichment.
/// Handles filter value resolution (tags, authors, etc.) with caching and async/sync dual interface.
/// </summary>
public abstract class PluginSearchQueryEnricher
{
    protected abstract string[] ResolvableFilterIds { get; }
    protected abstract TimeSpan FilterCacheTtl { get; }

    /// <summary>
    /// Async resolution entry point. Resolves filter values using the provided fetch function.
    /// </summary>
    public async Task<PluginSearchQuery> ResolveAsync(
        PluginSearchQuery query,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        if (query.Filters.Count == 0)
        {
            return query;
        }

        var resolvedFilters = new List<PluginSearchFilter>(query.Filters.Count);
        foreach (var filter in query.Filters)
        {
            var normalizedFilterId = filter.Id?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedFilterId))
            {
                continue;
            }

            var filterId = normalizedFilterId.ToLowerInvariant();
            if (filter.Values.Count == 0)
            {
                resolvedFilters.Add(filter);
                continue;
            }

            IReadOnlyList<string> resolvedValues;
            if (ResolvableFilterIds.Contains(filterId))
            {
                resolvedValues = await ResolveFilterValuesAsync(filterId, filter.Values, fetchAbsoluteUrlAsync, cancellationToken);
            }
            else
            {
                resolvedValues = filter.Values;
            }

            resolvedFilters.Add(new PluginSearchFilter(normalizedFilterId, resolvedValues, filter.Operation));
        }

        return query with { Filters = resolvedFilters };
    }

    /// <summary>
    /// Sync resolution entry point. Wraps ResolveAsync with synchronous adapter.
    /// </summary>
    public PluginSearchQuery Resolve(
        PluginSearchQuery query,
        Func<string, string?> fetchAbsoluteUrl)
    {
        return ResolveAsync(
                query,
                (absoluteUrl, _) => Task.FromResult(fetchAbsoluteUrl(absoluteUrl)),
                CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Provider-specific filter value resolution. Override to implement filter-specific logic.
    /// </summary>
    protected abstract Task<IReadOnlyList<string>> ResolveFilterValuesAsync(
        string filterId,
        IReadOnlyList<string> values,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken);

    /// <summary>
    /// Check if a value looks like a UUID (provider-specific format).
    /// Override to match provider's UUID pattern.
    /// </summary>
    protected abstract bool LooksLikeUuid(string value);

    /// <summary>
    /// Parse JSON catalog/lookup response. Override for provider-specific JSON structure.
    /// </summary>
    protected abstract IReadOnlyDictionary<string, string> ParseCatalogResponse(string payloadJson);

    /// <summary>
    /// Helper to resolve values using cached lookup (e.g., tag catalog).
    /// </summary>
    protected async Task<IReadOnlyList<string>> ResolveLookupValuesAsync(
        IReadOnlyList<string> values,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> getCatalogAsync,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        var lookup = await getCatalogAsync(cancellationToken);
        var resolved = new List<string>(values.Count);

        foreach (var value in values)
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (LooksLikeUuid(normalized))
            {
                resolved.Add(normalized);
                continue;
            }

            var key = normalized.ToLowerInvariant();
            if (lookup.TryGetValue(key, out var id) && !string.IsNullOrWhiteSpace(id))
            {
                resolved.Add(id);
                continue;
            }

            resolved.Add(normalized);
        }

        return resolved.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Helper to resolve values with per-item lookup (e.g., author/artist by name).
    /// Caches results in provided dictionary.
    /// </summary>
    protected async Task<IReadOnlyList<string>> ResolveWithCacheAsync(
        IReadOnlyList<string> values,
        ConcurrentDictionary<string, string?> cache,
        Func<string, CancellationToken, Task<string?>> resolveSingleAsync,
        CancellationToken cancellationToken)
    {
        var resolved = new List<string>(values.Count);

        foreach (var value in values)
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (LooksLikeUuid(normalized))
            {
                resolved.Add(normalized);
                continue;
            }

            var key = normalized.ToLowerInvariant();
            if (!cache.TryGetValue(key, out var id))
            {
                id = await resolveSingleAsync(normalized, cancellationToken);
                cache[key] = id;
            }

            resolved.Add(string.IsNullOrWhiteSpace(id) ? normalized : id);
        }

        return resolved.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Implements TTL-based caching for catalog data.
    /// </summary>
    protected sealed record CatalogCacheEntry(
        IReadOnlyDictionary<string, string> ValueByName,
        DateTimeOffset FetchedAtUtc);
}
