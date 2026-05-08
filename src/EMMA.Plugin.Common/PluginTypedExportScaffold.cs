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
}
