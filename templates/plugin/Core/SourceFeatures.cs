using System.Text.Json;
using EMMA.Plugin.Common;

namespace EMMA.TemplatePlugin.Core;

internal static class SourceFeatures
{
    private static readonly PluginDeferredSearchMetadataEnricher DeferredSearchMetadata = new();

    public static Task<IReadOnlyList<SearchItem>> EnrichSearchItemsAsync(
        IReadOnlyList<SearchItem> items,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(items);
    }

    public static IReadOnlyList<SearchItem> EnrichSearchItems(string enrichmentArgsJson)
    {
        return DeferredSearchMetadata.Enrich(
            enrichmentArgsJson,
            static _ => new Dictionary<string, List<MetadataItem>>(StringComparer.OrdinalIgnoreCase));
    }

    public static Task<IReadOnlyList<SearchSuggestionItem>> GetSearchSuggestionsAsync(
        SearchSuggestionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = request;
        return Task.FromResult<IReadOnlyList<SearchSuggestionItem>>([]);
    }

    public static IReadOnlyList<SearchSuggestionItem> GetSearchSuggestions(string requestJson)
    {
        var request = ParseSearchSuggestionRequest(requestJson);
        return request is null ? [] : [];
    }

    private static SearchSuggestionRequest? ParseSearchSuggestionRequest(string requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var controlId = PluginJsonElement.GetString(root, "controlId")?.Trim();
            if (string.IsNullOrWhiteSpace(controlId))
            {
                return null;
            }

            var query = PluginJsonElement.GetString(root, "query")?.Trim() ?? string.Empty;
            var limit = root.TryGetProperty("limit", out var limitElement) && limitElement.ValueKind == JsonValueKind.Number
                ? limitElement.GetInt32()
                : (int?)null;

            PluginSearchQuery? searchQuery = null;
            if (root.TryGetProperty("searchQuery", out var searchQueryElement)
                && searchQueryElement.ValueKind == JsonValueKind.Object)
            {
                searchQuery = PluginSearchQuery.Parse(searchQueryElement.GetRawText());
            }

            return new SearchSuggestionRequest(controlId, query, searchQuery, limit);
        }
        catch
        {
            return null;
        }
    }
}