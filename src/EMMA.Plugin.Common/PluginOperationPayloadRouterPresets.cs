namespace EMMA.Plugin.Common;

/// <summary>
/// Adds standard payload-hint registrations to a <see cref="PluginOperationPayloadRouter"/>.
/// </summary>
public static class PluginOperationPayloadRouterPresets
{
    /// <summary>
    /// Registers the standard paged-media payload hints used by WASM invoke routing.
    /// </summary>
    /// <param name="router">The payload router to configure.</param>
    /// <param name="searchHintResolver">The resolver that maps a parsed search query to a payload hint.</param>
    /// <param name="chaptersHintResolver">The resolver that maps a media id to a chapter payload hint.</param>
    /// <param name="atHomeHintResolver">The resolver that maps a chapter id to an at-home payload hint.</param>
    /// <param name="includeBenchmarkNetwork">When <see langword="true"/>, registers the benchmark-network payload hint to reuse search routing.</param>
    /// <returns>The configured payload router.</returns>
    public static PluginOperationPayloadRouter RegisterStandardPagedMediaHints(
        this PluginOperationPayloadRouter router,
        Func<PluginSearchQuery, string?> searchHintResolver,
        Func<string, string?> chaptersHintResolver,
        Func<string, string?> atHomeHintResolver,
        bool includeBenchmarkNetwork = true)
    {
        router
            .Register(PluginOperationNames.Search, request => searchHintResolver(PluginSearchQuery.Parse(request.argsJson)))
            .Register(PluginOperationNames.Chapters, request => chaptersHintResolver(request.ResolveMediaId()))
            .Register(PluginOperationNames.Page, request => atHomeHintResolver(request.ResolveChapterId()))
            .Register(PluginOperationNames.Pages, request => atHomeHintResolver(request.ResolveChapterId()));

        if (includeBenchmarkNetwork)
        {
            router.Register("benchmark-network", request => searchHintResolver(PluginSearchQuery.Parse(request.argsJson)));
        }

        return router;
    }
}