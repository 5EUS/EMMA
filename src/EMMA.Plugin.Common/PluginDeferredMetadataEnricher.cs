namespace EMMA.Plugin.Common;

/// <summary>
/// Generic base for deferred metadata enrichment patterns.
/// Standardizes on-demand metadata loading and merging across plugins.
/// </summary>
public abstract class PluginDeferredMetadataEnricher<TItem, TMetadata>
    where TItem : class
    where TMetadata : class
{
    /// <summary>
    /// Enrich items with metadata on-demand.
    /// </summary>
    public async Task<IReadOnlyList<TItem>> EnrichAsync(
        IEnumerable<TItem> items,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, TMetadata>>> fetchMetadataAsync,
        CancellationToken cancellationToken = default)
    {
        var itemList = items?.ToList() ?? [];
        if (itemList.Count == 0)
        {
            return itemList;
        }

        var ids = itemList.Select(ExtractId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (ids.Count == 0)
        {
            return itemList;
        }

        var metadataById = await fetchMetadataAsync(ids, cancellationToken);
        if (metadataById.Count == 0)
        {
            return itemList;
        }

        var enriched = new List<TItem>(itemList.Count);
        foreach (var item in itemList)
        {
            var id = ExtractId(item);
            if (string.IsNullOrWhiteSpace(id) || !metadataById.TryGetValue(id, out var metadata) || metadata == null)
            {
                enriched.Add(item);
                continue;
            }

            var enrichedItem = await EnrichItemAsync(item, metadata, cancellationToken);
            enriched.Add(enrichedItem);
        }

        return enriched;
    }

    /// <summary>
    /// Extract ID from item for metadata lookup.
    /// </summary>
    protected abstract string ExtractId(TItem item);

    /// <summary>
    /// Merge metadata into item. Override to implement merging logic.
    /// </summary>
    protected abstract Task<TItem> EnrichItemAsync(TItem item, TMetadata metadata, CancellationToken cancellationToken);

    /// <summary>
    /// Merge multiple metadata sources into item.
    /// </summary>
    protected async Task<TItem> EnrichItemAsync(
        TItem item,
        IReadOnlyList<TMetadata> metadataList,
        CancellationToken cancellationToken = default)
    {
        var result = item;
        foreach (var metadata in metadataList)
        {
            result = await EnrichItemAsync(result, metadata, cancellationToken);
        }

        return result;
    }
}

/// <summary>
/// Helper for merging metadata into strongly-typed structures.
/// </summary>
public static class PluginDeferredMetadataEnricherHelpers
{
    /// <summary>
    /// Merge multiple source dictionaries into target dictionary.
    /// </summary>
    public static void MergeDictionaries<TKey, TValue>(
        IDictionary<TKey, TValue> target,
        params IReadOnlyDictionary<TKey, TValue>[] sources)
        where TKey : notnull
    {
        foreach (var source in sources)
        {
            if (source == null)
            {
                continue;
            }

            foreach (var (key, value) in source)
            {
                if (!target.ContainsKey(key))
                {
                    target[key] = value;
                }
            }
        }
    }

    /// <summary>
    /// Merge metadata lists, avoiding duplicates by key.
    /// </summary>
    public static List<TItem> MergeLists<TItem, TKey>(
        IReadOnlyList<TItem> target,
        IReadOnlyList<TItem> source,
        Func<TItem, TKey> keySelector)
        where TKey : notnull
    {
        var seen = new HashSet<TKey>(target.Select(keySelector));
        var merged = new List<TItem>(target);

        foreach (var item in source)
        {
            var key = keySelector(item);
            seen.Add(key);
            merged.Add(item);
        }

        return merged;
    }
}
