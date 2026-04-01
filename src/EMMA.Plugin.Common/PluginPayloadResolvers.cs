namespace EMMA.Plugin.Common;

public static class PluginPayloadResolvers
{
    public static string ResolveProvidedOrFetched(string? payloadJson, Func<string?> fetch)
    {
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            return payloadJson;
        }

        return fetch() ?? string.Empty;
    }

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
}
