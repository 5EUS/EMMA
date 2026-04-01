namespace EMMA.Plugin.Common;

public static class PluginProviderUrlTemplates
{
    public static string? BuildSearchAbsoluteUrl(
        Uri baseUri,
        string searchPath,
        string queryParameterName,
        string? query,
        IReadOnlyList<string>? additionalParameters = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var parameters = new List<string>
        {
            $"{queryParameterName}={Uri.EscapeDataString(query.Trim())}"
        };

        if (additionalParameters is not null)
        {
            parameters.AddRange(additionalParameters);
        }

        return PluginUriUtilities.BuildAbsoluteUrl(baseUri, searchPath, parameters);
    }

    public static string? BuildResourceByIdAbsoluteUrl(
        Uri baseUri,
        string pathTemplate,
        string? id,
        IReadOnlyList<string>? queryParameters = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var encoded = PluginUriUtilities.EncodePathSegment(id);
        var path = pathTemplate.Replace("{id}", encoded, StringComparison.Ordinal);
        return PluginUriUtilities.BuildAbsoluteUrl(baseUri, path, queryParameters ?? []);
    }
}
