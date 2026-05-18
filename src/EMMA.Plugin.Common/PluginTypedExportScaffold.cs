namespace EMMA.Plugin.Common;

/// <summary>
/// Provides shared mapping and payload helpers for typed WASM export implementations.
/// </summary>
public static class PluginTypedExportScaffold
{
    /// <summary>
    /// Maps a list of input items into a new output list by using the supplied mapper.
    /// </summary>
    /// <param name="items">The input items to map.</param>
    /// <param name="mapper">The mapper that converts each input item.</param>
    /// <returns>A new list containing the mapped items.</returns>
    public static List<TOut> MapList<TIn, TOut>(IReadOnlyList<TIn> items, Func<TIn, TOut> mapper)
    {
        var result = new List<TOut>(items.Count);
        foreach (var item in items)
        {
            result.Add(mapper(item));
        }

        return result;
    }

    /// <summary>
    /// Maps an optional list of input items into a new output list by using the supplied mapper.
    /// </summary>
    /// <param name="items">The optional input items to map.</param>
    /// <param name="mapper">The mapper that converts each input item.</param>
    /// <returns>A new list containing the mapped items, or an empty list when <paramref name="items"/> is null or empty.</returns>
    public static List<TOut> MapOptionalList<TIn, TOut>(IReadOnlyList<TIn>? items, Func<TIn, TOut> mapper)
    {
        if (items is null || items.Count == 0)
        {
            return [];
        }

        return MapList(items, mapper);
    }

    /// <summary>
    /// Maps a nullable reference value when it is present.
    /// </summary>
    /// <param name="item">The optional item to map.</param>
    /// <param name="mapper">The mapper that converts the input item.</param>
    /// <returns>The mapped value when <paramref name="item"/> is present; otherwise, <see langword="null"/>.</returns>
    public static TOut? MapNullable<TIn, TOut>(TIn? item, Func<TIn, TOut> mapper)
        where TIn : class
        where TOut : class
    {
        return item is null ? null : mapper(item);
    }

    /// <summary>
    /// Resolves a search payload, invokes the search callback, and maps the returned items to a typed export surface.
    /// </summary>
    public static List<TWire> ResolveSearchResults<TDomain, TWire>(
        string query,
        string? payloadJson,
        Func<PluginSearchQuery, string?, string> payloadResolver,
        Func<string, string, IReadOnlyList<TDomain>> search,
        Func<TDomain, TWire> mapper)
    {
        var parsedQuery = PluginSearchQuery.Parse(query, fallbackQuery: query);
        var resolvedPayload = payloadResolver(parsedQuery, payloadJson);
        return MapList(search(query, resolvedPayload), mapper);
    }

    /// <summary>
    /// Resolves a chapter payload, invokes the chapter callback, and maps the returned items to a typed export surface.
    /// </summary>
    public static List<TWire> ResolveChapterResults<TDomain, TWire>(
        string mediaId,
        string? payloadJson,
        Func<string, string?, string> payloadResolver,
        Func<string, string, IReadOnlyList<TDomain>> chapters,
        Func<TDomain, TWire> mapper)
    {
        var resolvedPayload = payloadResolver(mediaId, payloadJson);
        return MapList(chapters(mediaId, resolvedPayload), mapper);
    }

    /// <summary>
    /// Resolves a page payload, invokes the page callback, and maps the returned item to a typed export surface.
    /// </summary>
    public static TWire? ResolvePageResult<TDomain, TWire>(
        string mediaId,
        string chapterId,
        uint pageIndex,
        string? payloadJson,
        Func<string, string?, string> payloadResolver,
        Func<string, string, uint, string, TDomain?> page,
        Func<TDomain, TWire> mapper)
        where TDomain : class
        where TWire : class
    {
        var resolvedPayload = payloadResolver(chapterId, payloadJson);
        return MapNullable(page(mediaId, chapterId, pageIndex, resolvedPayload), mapper);
    }

    /// <summary>
    /// Resolves a page-range payload, invokes the pages callback, and maps the returned items to a typed export surface.
    /// </summary>
    public static List<TWire> ResolvePageResults<TDomain, TWire>(
        string mediaId,
        string chapterId,
        uint startIndex,
        uint count,
        string? payloadJson,
        Func<string, string?, string> payloadResolver,
        Func<string, string, uint, uint, string, IReadOnlyList<TDomain>> pages,
        Func<TDomain, TWire> mapper)
    {
        var resolvedPayload = payloadResolver(chapterId, payloadJson);
        return MapList(pages(mediaId, chapterId, startIndex, count, resolvedPayload), mapper);
    }

    /// <summary>
    /// Resolves the payload used for an invoke request by applying router rules and host payload fallbacks.
    /// </summary>
    /// <param name="operation">The invoke operation name.</param>
    /// <param name="mediaId">The optional media identifier for the request.</param>
    /// <param name="mediaType">The optional media type for the request.</param>
    /// <param name="argsJson">The optional argument JSON for the request.</param>
    /// <param name="payloadJson">The optional provided payload JSON.</param>
    /// <param name="router">The payload router used to resolve fallback hints.</param>
    /// <param name="payloadProvider">The callback used to fetch payloads from the host.</param>
    /// <param name="useArgsJsonFallbackHint">When set to <see langword="true"/>, allows the router to fall back to <paramref name="argsJson"/> as the payload hint.</param>
    /// <returns>The resolved payload text, or <see langword="null"/> when the router returns no payload.</returns>
    public static string? ResolveInvokePayload(
        string? operation,
        string? mediaId,
        string? mediaType,
        string? argsJson,
        string? payloadJson,
        PluginOperationPayloadRouter router,
        Func<string, string?, string?> payloadProvider,
        bool useArgsJsonFallbackHint = true)
    {
        var request = new OperationRequest(operation ?? string.Empty, mediaId, mediaType, argsJson, payloadJson);
        return router.Resolve(request, payloadProvider, useArgsJsonFallbackHint);
    }

    /// <summary>
    /// Resolves a structured operation error from a serialized error string.
    /// </summary>
    /// <param name="error">The serialized error string to parse.</param>
    /// <param name="fallbackMessage">The fallback message to use when parsing yields no error details.</param>
    /// <returns>The parsed error information, or a generic failed error when parsing does not succeed.</returns>
    public static PluginOperationErrorInfo ResolveOperationError(
        string? error,
        string fallbackMessage = "operation failed")
    {
        if (PluginOperationError.TryParse(error, out var parsed))
        {
            return parsed;
        }

        return new PluginOperationErrorInfo(PluginOperationErrorKind.Failed, fallbackMessage);
    }

    /// <summary>
    /// Maps a serialized operation error into a caller-defined exception or error payload.
    /// </summary>
    public static TError MapOperationError<TError>(
        string? error,
        Func<string, TError> unsupportedFactory,
        Func<string, TError> invalidArgumentsFactory,
        Func<string, TError> failedFactory)
    {
        var parsed = ResolveOperationError(error);

        return parsed.Kind switch
        {
            PluginOperationErrorKind.UnsupportedOperation => unsupportedFactory(parsed.Message),
            PluginOperationErrorKind.InvalidArguments => invalidArgumentsFactory(parsed.Message),
            _ => failedFactory(parsed.Message)
        };
    }

    /// <summary>
    /// Converts a generic operation result into a typed response, throwing a caller-defined error when the operation failed.
    /// </summary>
    public static TResponse ToOperationResponseOrThrow<TResponse, TError>(
        OperationResult result,
        Func<string, string, TResponse> responseFactory,
        Func<string, TError> unsupportedFactory,
        Func<string, TError> invalidArgumentsFactory,
        Func<string, TError> failedFactory)
        where TError : Exception
    {
        if (result.isError)
        {
            throw MapOperationError(
                result.error,
                unsupportedFactory,
                invalidArgumentsFactory,
                failedFactory);
        }

        return responseFactory(result.contentType, result.payloadJson);
    }

    /// <summary>
    /// Executes a typed invoke wrapper and converts unexpected exceptions into a caller-defined failed operation error.
    /// </summary>
    public static TResponse InvokeWithOperationErrorHandling<TResponse, TError>(
        Func<TResponse> invoke,
        Func<string, TError> failedFactory,
        string fallbackMessage = "operation failed")
        where TError : Exception
    {
        try
        {
            return invoke();
        }
        catch (Exception ex) when (ex is not TError)
        {
            throw failedFactory(string.IsNullOrWhiteSpace(ex.Message) ? fallbackMessage : ex.Message);
        }
    }
}
