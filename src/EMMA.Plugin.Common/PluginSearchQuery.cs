using System.Text.Json;

namespace EMMA.Plugin.Common;

public sealed record PluginSearchFilter(string Id, IReadOnlyList<string> Values, string? Operation);

public sealed record PluginSearchQueryAddition(string Id, string Value, string? Type);

public sealed record PluginSearchQuery(
    string Query,
    IReadOnlyList<string> MediaTypes,
    IReadOnlyList<PluginSearchFilter> Filters,
    IReadOnlyList<PluginSearchQueryAddition> QueryAdditions,
    string? Sort,
    int? Page,
    int? PageSize)
{
    public IReadOnlyList<string> GetFilterValues(string filterId)
    {
        var match = Filters.FirstOrDefault(filter => string.Equals(filter.Id, filterId, StringComparison.OrdinalIgnoreCase));
        return match?.Values ?? [];
    }

    public string GetQueryAddition(string additionId)
    {
        var match = QueryAdditions.FirstOrDefault(addition => string.Equals(addition.Id, additionId, StringComparison.OrdinalIgnoreCase));
        return match?.Value ?? string.Empty;
    }

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