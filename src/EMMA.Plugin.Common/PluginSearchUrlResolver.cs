namespace EMMA.Plugin.Common;

/// <summary>
/// Resolves search URLs after applying provider-specific query enrichment.
/// </summary>
public static class PluginSearchUrlResolver
{
    /// <summary>
    /// Resolves a final search URL by enriching a parsed query and then passing it to a provider-specific URL builder.
    /// </summary>
    /// <param name="parsedQuery">The parsed query to enrich.</param>
    /// <param name="queryResolver">The callback that resolves provider-specific query details by using a payload fetcher.</param>
    /// <param name="searchUrlBuilder">The callback that builds the final search URL from the resolved query.</param>
    /// <param name="payloadProvider">The callback used to fetch payloads needed during query resolution.</param>
    /// <param name="operation">The operation name used when requesting payloads.</param>
    /// <returns>The resolved absolute search URL, or <see langword="null"/> when the builder cannot produce one.</returns>
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

    /// <summary>
    /// Resolves a final search URL by enriching a parsed query by using a shared payload source.
    /// </summary>
    public static string? ResolveSearchAbsoluteUrl(
        PluginSearchQuery parsedQuery,
        Func<PluginSearchQuery, PluginPayloadSource, PluginSearchQuery> queryResolver,
        Func<PluginSearchQuery, string?> searchUrlBuilder,
        PluginPayloadSource payloadSource)
    {
        ArgumentNullException.ThrowIfNull(payloadSource);

        var resolvedQuery = queryResolver(parsedQuery, payloadSource);
        return searchUrlBuilder(resolvedQuery);
    }
}
