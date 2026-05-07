namespace EMMA.Plugin.Common;

public static class PluginSearchUrlResolver
{
    public static string? ResolveSearchAbsoluteUrl(
        PluginSearchQuery parsedQuery,
        Func<PluginSearchQuery, Func<string, string>, PluginSearchQuery> queryResolver,
        Func<PluginSearchQuery, string?> searchUrlBuilder,
        Func<string, string?, string?> payloadProvider,
        string operation = PluginOperationNames.Search)
    {
        var resolvedQuery = queryResolver(
            parsedQuery,
            absoluteUrl => payloadProvider(operation, absoluteUrl) ?? string.Empty);

        return searchUrlBuilder(resolvedQuery);
    }
}
