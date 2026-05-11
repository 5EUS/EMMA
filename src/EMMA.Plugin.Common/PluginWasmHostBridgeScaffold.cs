namespace EMMA.Plugin.Common;

/// <summary>
/// Provides reusable host-bridge payload helpers for WASM plugins.
/// </summary>
public static class PluginWasmHostBridgeScaffold
{
    /// <summary>
    /// Fetches payload content from the host bridge and normalizes the returned JSON.
    /// </summary>
    /// <param name="payloadHint">The payload hint or absolute URL used by the host bridge.</param>
    /// <param name="payloadProvider">The host bridge callback that fetches payload content.</param>
    /// <param name="operation">The operation name used for host payload lookup.</param>
    /// <returns>The normalized payload, or <see langword="null"/> when no payload can be fetched.</returns>
    public static string? FetchPayload(
        string? payloadHint,
        Func<string, string?, string?> payloadProvider,
        string operation = PluginOperationNames.Search)
    {
        if (string.IsNullOrWhiteSpace(payloadHint))
        {
            return null;
        }

        try
        {
            return PluginPayload.NormalizePayload(payloadProvider(operation, payloadHint));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a search payload by preferring the provided payload and otherwise fetching from the host bridge.
    /// </summary>
    /// <param name="payloadJson">The provided payload value.</param>
    /// <param name="parsedQuery">The parsed search query.</param>
    /// <param name="queryResolver">The provider-specific search query resolver.</param>
    /// <param name="searchUrlBuilder">The provider-specific search URL builder.</param>
    /// <param name="payloadProvider">The host bridge callback that fetches payload content.</param>
    /// <param name="operation">The operation name used for host payload lookup.</param>
    /// <returns>The resolved payload content.</returns>
    public static string ResolveSearchPayload(
        string? payloadJson,
        PluginSearchQuery parsedQuery,
        Func<PluginSearchQuery, Func<string, string>, PluginSearchQuery> queryResolver,
        Func<PluginSearchQuery, string?> searchUrlBuilder,
        Func<string, string?, string?> payloadProvider,
        string operation = PluginOperationNames.Search)
    {
        return PluginPayloadResolvers.ResolveProvidedOrHostPayload(
            payloadJson,
            operation,
            () => PluginSearchUrlResolver.ResolveSearchAbsoluteUrl(
                parsedQuery,
                queryResolver,
                searchUrlBuilder,
                payloadProvider,
                operation),
            payloadProvider);
    }

    /// <summary>
    /// Resolves and merges paged chapter feed payloads by using the host bridge for subsequent pages.
    /// </summary>
    /// <param name="mediaId">The media identifier whose chapter feed is being loaded.</param>
    /// <param name="payloadJson">The initial payload value, if one was provided.</param>
    /// <param name="chapterFeedPageSize">The provider page size used to fetch subsequent chapter pages.</param>
    /// <param name="chapterFeedMaxPages">The maximum number of chapter feed pages to merge.</param>
    /// <param name="chaptersUrlBuilder">The callback that builds chapter feed URLs.</param>
    /// <param name="payloadProvider">The host bridge callback that fetches payload content.</param>
    /// <param name="operation">The operation name used for host payload lookup.</param>
    /// <returns>The merged chapter feed payload.</returns>
    public static string ResolveMergedChapterFeedPayload(
        string mediaId,
        string? payloadJson,
        int chapterFeedPageSize,
        int chapterFeedMaxPages,
        Func<string, int, int, string?> chaptersUrlBuilder,
        Func<string, string?, string?> payloadProvider,
        string operation = PluginOperationNames.Chapters)
    {
        var firstPayload = PluginPayloadResolvers.ResolveProvidedOrHostPayload(
            payloadJson,
            operation,
            () => chaptersUrlBuilder(mediaId, chapterFeedPageSize, 0),
            payloadProvider);

        if (string.IsNullOrWhiteSpace(firstPayload))
        {
            return string.Empty;
        }

        return PluginWasmPagingJsonHelpers.MergeChapterFeedPages(
            firstPayload,
            chapterFeedMaxPages,
            nextOffset => FetchPayload(
                chaptersUrlBuilder(mediaId, chapterFeedPageSize, nextOffset),
                payloadProvider,
                operation));
    }
}