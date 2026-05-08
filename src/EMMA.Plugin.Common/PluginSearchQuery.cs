using System.Text.Json;

namespace EMMA.Plugin.Common;

/// <summary>
/// Represents a search filter and its requested values.
/// </summary>
/// <param name="Id">The filter identifier.</param>
/// <param name="Values">The requested filter values.</param>
/// <param name="Operation">The optional filter operation.</param>
public sealed record PluginSearchFilter(string Id, IReadOnlyList<string> Values, string? Operation);

/// <summary>
/// Represents an additional named query value used by provider-specific search logic.
/// </summary>
/// <param name="Id">The addition identifier.</param>
/// <param name="Value">The addition value.</param>
/// <param name="Type">The optional addition type.</param>
public sealed record PluginSearchQueryAddition(string Id, string Value, string? Type);

/// <summary>
/// Represents a parsed plugin search request.
/// </summary>
/// <param name="Query">The search text.</param>
/// <param name="MediaTypes">The requested media types.</param>
/// <param name="Filters">The requested structured filters.</param>
/// <param name="QueryAdditions">The provider-specific query additions.</param>
/// <param name="Sort">The optional sort identifier.</param>
/// <param name="Page">The optional requested page number.</param>
/// <param name="PageSize">The optional requested page size.</param>
public sealed record PluginSearchQuery(
    string Query,
    IReadOnlyList<string> MediaTypes,
    IReadOnlyList<PluginSearchFilter> Filters,
    IReadOnlyList<PluginSearchQueryAddition> QueryAdditions,
    string? Sort,
    int? Page,
    int? PageSize)
{
    /// <summary>
    /// Gets the values for a search filter by identifier.
    /// </summary>
    /// <param name="filterId">The filter identifier to look up.</param>
    /// <returns>The matching filter values, or an empty list when the filter is absent.</returns>
    public IReadOnlyList<string> GetFilterValues(string filterId)
    {
        var match = Filters.FirstOrDefault(filter => string.Equals(filter.Id, filterId, StringComparison.OrdinalIgnoreCase));
        return match?.Values ?? [];
    }

    /// <summary>
    /// Gets the value for a query addition by identifier.
    /// </summary>
    /// <param name="additionId">The query addition identifier to look up.</param>
    /// <returns>The query addition value, or an empty string when the addition is absent.</returns>
    public string GetQueryAddition(string additionId)
    {
        var match = QueryAdditions.FirstOrDefault(addition => string.Equals(addition.Id, additionId, StringComparison.OrdinalIgnoreCase));
        return match?.Value ?? string.Empty;
    }

    /// <summary>
    /// Parses a search query object from operation argument JSON.
    /// </summary>
    /// <param name="argsJson">The argument JSON to parse.</param>
    /// <param name="fallbackQuery">An optional fallback query string to use when the JSON does not provide one.</param>
    /// <returns>The parsed search query, or a default query when parsing fails.</returns>
    public static PluginSearchQuery Parse(string? argsJson, string? fallbackQuery = null)
    {
        var defaultQuery = fallbackQuery ?? string.Empty;
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return new PluginSearchQuery(defaultQuery, [], [], [], null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new PluginSearchQuery(defaultQuery, [], [], [], null, null, null);
            }

            var query = ReadString(root, "query") ?? defaultQuery;
            var mediaTypes = ReadStringArray(root, "mediaTypes");
            var filters = ReadFilters(root);
            var queryAdditions = ReadQueryAdditions(root);
            var sort = ReadString(root, "sort");
            var page = ReadInt32(root, "page");
            var pageSize = ReadInt32(root, "pageSize");

            return new PluginSearchQuery(query, mediaTypes, filters, queryAdditions, sort, page, pageSize);
        }
        catch
        {
            return new PluginSearchQuery(defaultQuery, [], [], [], null, null, null);
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())];
    }

    private static IReadOnlyList<PluginSearchFilter> ReadFilters(JsonElement root)
    {
        if (!root.TryGetProperty("filters", out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<PluginSearchFilter>();
        foreach (var filter in element.EnumerateArray())
        {
            if (filter.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadString(filter, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var values = ReadStringArray(filter, "values");
            var operation = ReadString(filter, "operation");
            list.Add(new PluginSearchFilter(id, values, operation));
        }

        return list;
    }

    private static IReadOnlyList<PluginSearchQueryAddition> ReadQueryAdditions(JsonElement root)
    {
        if (!root.TryGetProperty("queryAdditions", out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<PluginSearchQueryAddition>();
        foreach (var addition in element.EnumerateArray())
        {
            if (addition.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadString(addition, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var value = ReadString(addition, "value") ?? string.Empty;
            var type = ReadString(addition, "type");
            list.Add(new PluginSearchQueryAddition(id, value, type));
        }

        return list;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static int? ReadInt32(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            return numeric;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }
}