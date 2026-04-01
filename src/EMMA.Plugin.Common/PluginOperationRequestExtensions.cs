namespace EMMA.Plugin.Common;

public static class PluginOperationRequestExtensions
{
    public static string NormalizedOperation(this OperationRequest request)
    {
        return request.operation?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    public static string ResolveMediaId(this OperationRequest request, string argsKey = "mediaId")
    {
        return !string.IsNullOrWhiteSpace(request.mediaId)
            ? request.mediaId
            : PluginJsonArgs.GetString(request.argsJson, argsKey);
    }

    public static string ResolveChapterId(this OperationRequest request, string argsKey = "chapterId")
    {
        return PluginJsonArgs.GetString(request.argsJson, argsKey);
    }

    public static uint ResolvePageIndex(this OperationRequest request, string argsKey = "pageIndex", uint defaultValue = 0)
    {
        return PluginJsonArgs.GetUInt32(request.argsJson, argsKey) ?? defaultValue;
    }

    public static uint ResolveStartIndex(this OperationRequest request, string argsKey = "startIndex", uint defaultValue = 0)
    {
        return PluginJsonArgs.GetUInt32(request.argsJson, argsKey) ?? defaultValue;
    }

    public static uint ResolveCount(this OperationRequest request, string argsKey = "count", uint defaultValue = 0)
    {
        return PluginJsonArgs.GetUInt32(request.argsJson, argsKey) ?? defaultValue;
    }

    public static bool IsPagedMediaRequest(this OperationRequest request)
    {
        return string.IsNullOrWhiteSpace(request.mediaType)
            || string.Equals(request.mediaType.Trim(), PluginMediaTypes.Paged, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVideoMediaRequest(this OperationRequest request)
    {
        return string.IsNullOrWhiteSpace(request.mediaType)
            || string.Equals(request.mediaType.Trim(), PluginMediaTypes.Video, StringComparison.OrdinalIgnoreCase);
    }
}
