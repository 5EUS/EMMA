namespace EMMA.Plugin.Common;

/// <summary>
/// Registers common paged and video operation combinations on a dispatcher.
/// </summary>
public static class PluginOperationDispatcherPresets
{
    /// <summary>
    /// Registers the standard paged-media operation handlers on a dispatcher.
    /// </summary>
    /// <param name="dispatcher">The dispatcher to configure.</param>
    /// <param name="search">The handler for search requests.</param>
    /// <param name="chapters">The handler for chapter listing requests.</param>
    /// <param name="page">The handler for single-page requests.</param>
    /// <param name="pages">The handler for page-range requests.</param>
    /// <param name="supportsChapterRequests">An optional predicate that determines whether the current request supports paged chapter operations.</param>
    /// <returns>The configured dispatcher.</returns>
    public static PluginOperationDispatcher RegisterPagedOperations(
        this PluginOperationDispatcher dispatcher,
        Func<OperationRequest, OperationResult> search,
        Func<OperationRequest, OperationResult> chapters,
        Func<OperationRequest, OperationResult> page,
        Func<OperationRequest, OperationResult> pages,
        Func<OperationRequest, bool>? supportsChapterRequests = null)
    {
        supportsChapterRequests ??= request => request.IsPagedMediaRequest();

        return dispatcher
            .Register(PluginOperationNames.Search, search)
            .Register(PluginOperationNames.Chapters, request =>
                supportsChapterRequests(request)
                    ? chapters(request)
                    : OperationResult.UnsupportedOperation(request.NormalizedOperation()))
            .Register(PluginOperationNames.Page, request =>
                supportsChapterRequests(request)
                    ? page(request)
                    : OperationResult.UnsupportedOperation(request.NormalizedOperation()))
            .Register(PluginOperationNames.Pages, request =>
                supportsChapterRequests(request)
                    ? pages(request)
                    : OperationResult.UnsupportedOperation(request.NormalizedOperation()));
    }

    /// <summary>
    /// Registers the standard video-media operation handlers on a dispatcher.
    /// </summary>
    /// <param name="dispatcher">The dispatcher to configure.</param>
    /// <param name="videoStreams">The handler for video stream listing requests.</param>
    /// <param name="videoSegment">The handler for video segment requests.</param>
    /// <param name="supportsVideoRequests">An optional predicate that determines whether the current request supports video operations.</param>
    /// <returns>The configured dispatcher.</returns>
    public static PluginOperationDispatcher RegisterVideoOperations(
        this PluginOperationDispatcher dispatcher,
        Func<OperationRequest, OperationResult> videoStreams,
        Func<OperationRequest, OperationResult> videoSegment,
        Func<OperationRequest, bool>? supportsVideoRequests = null)
    {
        supportsVideoRequests ??= request => request.IsVideoMediaRequest();

        return dispatcher
            .Register(PluginOperationNames.VideoStreams, request =>
                supportsVideoRequests(request)
                    ? videoStreams(request)
                    : OperationResult.UnsupportedOperation(request.NormalizedOperation()))
            .Register(PluginOperationNames.VideoSegment, request =>
                supportsVideoRequests(request)
                    ? videoSegment(request)
                    : OperationResult.UnsupportedOperation(request.NormalizedOperation()));
    }
}
