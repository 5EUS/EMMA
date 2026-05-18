namespace EMMA.Plugin.Common;

/// <summary>
/// Provides convenience accessors for <see cref="OperationRequest"/> values.
/// </summary>
public static class PluginOperationRequestExtensions
{
    /// <summary>
    /// Normalizes the operation name on a request for case-insensitive dispatch.
    /// </summary>
    /// <param name="request">The request whose operation name should be normalized.</param>
    /// <returns>The trimmed, lowercase operation name, or an empty string when the request has no operation.</returns>
    public static string NormalizedOperation(this OperationRequest request)
    {
        return request.operation?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    /// <summary>
    /// Resolves the media identifier from the request or its argument JSON.
    /// </summary>
    /// <param name="request">The request to inspect.</param>
    /// <param name="argsKey">The argument JSON property to use when the direct media identifier is missing.</param>
    /// <returns>The resolved media identifier, or an empty string when none is available.</returns>
    public static string ResolveMediaId(this OperationRequest request, string argsKey = "mediaId")
    {
        return !string.IsNullOrWhiteSpace(request.mediaId)
            ? request.mediaId
            : PluginJsonArgs.GetString(request.argsJson, argsKey);
    }

    /// <summary>
    /// Resolves the chapter identifier from the request argument JSON.
    /// </summary>
    /// <param name="request">The request to inspect.</param>
    /// <param name="argsKey">The argument JSON property that contains the chapter identifier.</param>
    /// <returns>The resolved chapter identifier, or an empty string when none is available.</returns>
    public static string ResolveChapterId(this OperationRequest request, string argsKey = "chapterId")
    {
        return PluginJsonArgs.GetString(request.argsJson, argsKey);
    }

    /// <summary>
    /// Resolves the requested page index from the request argument JSON.
    /// </summary>
    /// <param name="request">The request to inspect.</param>
    /// <param name="argsKey">The argument JSON property that contains the page index.</param>
    /// <param name="defaultValue">The value to use when the argument is missing or invalid.</param>
    /// <returns>The resolved page index.</returns>
    public static uint ResolvePageIndex(this OperationRequest request, string argsKey = "pageIndex", uint defaultValue = 0)
    {
        return PluginJsonArgs.GetUInt32(request.argsJson, argsKey) ?? defaultValue;
    }

    /// <summary>
    /// Resolves the requested page start index from the request argument JSON.
    /// </summary>
    /// <param name="request">The request to inspect.</param>
    /// <param name="argsKey">The argument JSON property that contains the start index.</param>
    /// <param name="defaultValue">The value to use when the argument is missing or invalid.</param>
    /// <returns>The resolved start index.</returns>
    public static uint ResolveStartIndex(this OperationRequest request, string argsKey = "startIndex", uint defaultValue = 0)
    {
        return PluginJsonArgs.GetUInt32(request.argsJson, argsKey) ?? defaultValue;
    }

    /// <summary>
    /// Resolves the requested item count from the request argument JSON.
    /// </summary>
    /// <param name="request">The request to inspect.</param>
    /// <param name="argsKey">The argument JSON property that contains the count.</param>
    /// <param name="defaultValue">The value to use when the argument is missing or invalid.</param>
    /// <returns>The resolved count.</returns>
    public static uint ResolveCount(this OperationRequest request, string argsKey = "count", uint defaultValue = 0)
    {
        return PluginJsonArgs.GetUInt32(request.argsJson, argsKey) ?? defaultValue;
    }

    /// <summary>
    /// Determines whether a request targets paged media, treating an unspecified media type as paged-compatible.
    /// </summary>
    /// <param name="request">The request to inspect.</param>
    /// <returns><see langword="true"/> when the request is compatible with paged media operations; otherwise, <see langword="false"/>.</returns>
    public static bool IsPagedMediaRequest(this OperationRequest request)
    {
        return string.IsNullOrWhiteSpace(request.mediaType)
            || string.Equals(request.mediaType.Trim(), PluginMediaTypes.Paged, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether a request targets video media, treating an unspecified media type as video-compatible.
    /// </summary>
    /// <param name="request">The request to inspect.</param>
    /// <returns><see langword="true"/> when the request is compatible with video operations; otherwise, <see langword="false"/>.</returns>
    public static bool IsVideoMediaRequest(this OperationRequest request)
    {
        return string.IsNullOrWhiteSpace(request.mediaType)
            || string.Equals(request.mediaType.Trim(), PluginMediaTypes.Video, StringComparison.OrdinalIgnoreCase);
    }
}
