namespace EMMA.Plugin.Common;

/// <summary>
/// Provides fallback strategies for resolving JSON payload content.
/// </summary>
public static class PluginPayloadResolvers
{
    /// <summary>
    /// Returns the provided payload when present, or fetches one from the supplied callback.
    /// </summary>
    /// <param name="payloadJson">The provided payload value.</param>
    /// <param name="fetch">The callback used to fetch a fallback payload.</param>
    /// <returns>The provided payload or the fetched fallback payload.</returns>
    public static string ResolveProvidedOrFetched(string? payloadJson, Func<string?> fetch)
    {
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            return payloadJson;
        }

        return fetch() ?? string.Empty;
    }

    /// <summary>
    /// Returns the provided payload when present, or fetches one from the host by using a supplied payload hint.
    /// </summary>
    /// <param name="payloadJson">The provided payload value.</param>
    /// <param name="operation">The operation name used for host payload lookup.</param>
    /// <param name="payloadHint">The payload hint passed to the host payload provider.</param>
    /// <param name="payloadProvider">The callback used to fetch payload content from the host.</param>
    /// <returns>The provided payload or the host-provided payload.</returns>
    public static string ResolveProvidedOrHostPayload(
        string? payloadJson,
        string operation,
        string? payloadHint,
        Func<string, string?, string?> payloadProvider)
    {
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            return payloadJson;
        }

        if (string.IsNullOrWhiteSpace(payloadHint))
        {
            return string.Empty;
        }

        return payloadProvider(operation, payloadHint) ?? string.Empty;
    }

    /// <summary>
    /// Returns the provided payload when present, or fetches one from the host by using a lazily computed payload hint.
    /// </summary>
    /// <param name="payloadJson">The provided payload value.</param>
    /// <param name="operation">The operation name used for host payload lookup.</param>
    /// <param name="payloadHintFactory">The callback that creates the payload hint when a fallback is needed.</param>
    /// <param name="payloadProvider">The callback used to fetch payload content from the host.</param>
    /// <returns>The provided payload or the host-provided payload.</returns>
    public static string ResolveProvidedOrHostPayload(
        string? payloadJson,
        string operation,
        Func<string?> payloadHintFactory,
        Func<string, string?, string?> payloadProvider)
    {
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            return payloadJson;
        }

        var payloadHint = payloadHintFactory();
        if (string.IsNullOrWhiteSpace(payloadHint))
        {
            return string.Empty;
        }

        return payloadProvider(operation, payloadHint) ?? string.Empty;
    }
}
