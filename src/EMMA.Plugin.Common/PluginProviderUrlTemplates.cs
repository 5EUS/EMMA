namespace EMMA.Plugin.Common;

/// <summary>
/// Builds common provider URL shapes from base URIs and templates.
/// </summary>
public static class PluginProviderUrlTemplates
{
    /// <summary>
    /// Builds a search URL from a provider base URI, path, and query parameter name.
    /// </summary>
    /// <param name="baseUri">The provider base URI.</param>
    /// <param name="searchPath">The relative search path.</param>
    /// <param name="queryParameterName">The query parameter name that carries the search text.</param>
    /// <param name="query">The search text to encode.</param>
    /// <param name="additionalParameters">Optional additional query parameters to append.</param>
    /// <returns>The absolute search URL, or <see langword="null"/> when the query is empty.</returns>
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

    /// <summary>
    /// Builds a resource URL by replacing the <c>{id}</c> placeholder in a path template.
    /// </summary>
    /// <param name="baseUri">The provider base URI.</param>
    /// <param name="pathTemplate">The relative path template that contains an <c>{id}</c> placeholder.</param>
    /// <param name="id">The resource identifier to insert into the template.</param>
    /// <param name="queryParameters">Optional query parameters to append.</param>
    /// <returns>The absolute resource URL, or <see langword="null"/> when the identifier is empty.</returns>
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
