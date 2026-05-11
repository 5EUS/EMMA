using System.Text.Json;

namespace EMMA.Plugin.Common;

/// <summary>
/// Higher-level helper for deferred search-item metadata enrichment.
/// </summary>
public sealed class PluginDeferredSearchMetadataEnricher
{
    /// <summary>
    /// Enriches search items described by invoke-style enrichment args.
    /// </summary>
    public IReadOnlyList<SearchItem> Enrich(
        string enrichmentArgsJson,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, List<MetadataItem>>> fetchMetadata)
    {
        ArgumentNullException.ThrowIfNull(fetchMetadata);

        var request = ParseRequest(enrichmentArgsJson);
        if (request.ItemIds.Count == 0)
        {
            return [];
        }

        var metadataById = fetchMetadata(request.ItemIds);
        return MergeItems(request.ItemIds, request.BaseItems, metadataById);
    }

    /// <summary>
    /// Enriches search items described by invoke-style enrichment args asynchronously.
    /// </summary>
    public async Task<IReadOnlyList<SearchItem>> EnrichAsync(
        string enrichmentArgsJson,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, List<MetadataItem>>>> fetchMetadataAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetchMetadataAsync);

        var request = ParseRequest(enrichmentArgsJson);
        if (request.ItemIds.Count == 0)
        {
            return [];
        }

        var metadataById = await fetchMetadataAsync(request.ItemIds, cancellationToken);
        return MergeItems(request.ItemIds, request.BaseItems, metadataById);
    }

    /// <summary>
    /// Enriches an existing set of search items asynchronously.
    /// </summary>
    public async Task<IReadOnlyList<SearchItem>> EnrichAsync(
        IReadOnlyList<SearchItem> items,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, List<MetadataItem>>>> fetchMetadataAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(fetchMetadataAsync);

        if (items.Count == 0)
        {
            return items;
        }

        var itemIds = ExtractItemIds(items);
        if (itemIds.Count == 0)
        {
            return items;
        }

        var metadataById = await fetchMetadataAsync(itemIds, cancellationToken);
        return MergeItems(itemIds, items, metadataById);
    }

    /// <summary>
    /// Parses invoke-style enrichment arguments.
    /// </summary>
    public SearchEnrichmentRequest ParseRequest(string enrichmentArgsJson)
    {
        List<string> itemIds = [];
        IReadOnlyList<SearchItem>? baseItems = null;

        if (!string.IsNullOrWhiteSpace(enrichmentArgsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(enrichmentArgsJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("itemIds", out var idsElement) && idsElement.ValueKind == JsonValueKind.Array)
                {
                    itemIds = [.. idsElement.EnumerateArray()
                        .Select(static el => el.GetString() ?? string.Empty)
                        .Where(static id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)];
                }

                if (root.TryGetProperty("baseItems", out var baseItemsElement) && baseItemsElement.ValueKind == JsonValueKind.Array)
                {
                    var baseItemsList = new List<SearchItem>();
                    foreach (var item in baseItemsElement.EnumerateArray())
                    {
                        var id = PluginJsonElement.GetString(item, "id");
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            continue;
                        }

                        var source = PluginJsonElement.GetString(item, "source") ?? string.Empty;
                        var title = PluginJsonElement.GetString(item, "title") ?? string.Empty;
                        var mediaType = PluginJsonElement.GetString(item, "mediaType") ?? string.Empty;
                        var thumbnailUrl = PluginJsonElement.GetString(item, "thumbnailUrl");
                        var description = PluginJsonElement.GetString(item, "description");

                        IReadOnlyList<MetadataItem>? metadata = null;
                        if (item.TryGetProperty("metadata", out var metadataElement) && metadataElement.ValueKind == JsonValueKind.Array)
                        {
                            var metadataList = new List<MetadataItem>();
                            foreach (var meta in metadataElement.EnumerateArray())
                            {
                                var key = PluginJsonElement.GetString(meta, "key");
                                var value = PluginJsonElement.GetString(meta, "value");
                                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                                {
                                    metadataList.Add(new MetadataItem(key, value));
                                }
                            }

                            metadata = metadataList.Count > 0 ? metadataList : null;
                        }

                        baseItemsList.Add(new SearchItem(id, source, title, mediaType, thumbnailUrl, description, metadata));
                    }

                    baseItems = baseItemsList.Count > 0 ? baseItemsList : null;
                }
            }
            catch
            {
            }
        }

        return new SearchEnrichmentRequest(itemIds, baseItems);
    }

    /// <summary>
    /// Merges additional metadata into search items while preserving input order.
    /// </summary>
    public IReadOnlyList<SearchItem> MergeItems(
        IReadOnlyList<string> itemIds,
        IReadOnlyList<SearchItem>? baseItems,
        IReadOnlyDictionary<string, List<MetadataItem>> metadataById)
    {
        if (itemIds.Count == 0)
        {
            return [];
        }

        if (metadataById.Count == 0)
        {
            return baseItems ?? [.. itemIds.Select(id => new SearchItem(id, string.Empty, string.Empty, string.Empty, null, null, null))];
        }

        var enriched = new List<SearchItem>(baseItems?.Count ?? itemIds.Count);
        if (baseItems is not null)
        {
            foreach (var item in baseItems)
            {
                var metadata = item.metadata is null
                    ? []
                    : new List<MetadataItem>(item.metadata);

                if (metadataById.TryGetValue(item.id, out var extraMetadata) && extraMetadata.Count > 0)
                {
                    metadata.AddRange(extraMetadata);
                }

                enriched.Add(item with { metadata = metadata.Count > 0 ? metadata : item.metadata });
            }

            return enriched;
        }

        foreach (var id in itemIds)
        {
            var metadata = new List<MetadataItem>();
            if (metadataById.TryGetValue(id, out var extraMetadata) && extraMetadata.Count > 0)
            {
                metadata.AddRange(extraMetadata);
            }

            enriched.Add(new SearchItem(id, string.Empty, string.Empty, string.Empty, null, null, metadata.Count > 0 ? metadata : null));
        }

        return enriched;
    }

    /// <summary>
    /// Extracts distinct item identifiers from a set of search items.
    /// </summary>
    public static IReadOnlyList<string> ExtractItemIds(IReadOnlyList<SearchItem> items)
    {
        return [.. items
            .Select(static item => item.id)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Parsed invoke-style search enrichment request.
    /// </summary>
    public sealed record SearchEnrichmentRequest(
        IReadOnlyList<string> ItemIds,
        IReadOnlyList<SearchItem>? BaseItems);
}