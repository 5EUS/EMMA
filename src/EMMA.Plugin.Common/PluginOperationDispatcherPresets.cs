namespace EMMA.Plugin.Common;

public static class PluginOperationDispatcherPresets
{
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
