namespace EMMA.Plugin.Common;

public static class PluginTypedExportScaffold
{
    public static List<TOut> MapList<TIn, TOut>(IReadOnlyList<TIn> items, Func<TIn, TOut> mapper)
    {
        var result = new List<TOut>(items.Count);
        foreach (var item in items)
        {
            result.Add(mapper(item));
        }

        return result;
    }

    public static TOut? MapNullable<TIn, TOut>(TIn? item, Func<TIn, TOut> mapper)
        where TIn : class
        where TOut : class
    {
        return item is null ? null : mapper(item);
    }

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
        var request = new OperationRequest(operation, mediaId, mediaType, argsJson, payloadJson);
        return router.Resolve(request, payloadProvider, useArgsJsonFallbackHint);
    }

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
