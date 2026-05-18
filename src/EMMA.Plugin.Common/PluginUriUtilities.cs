namespace EMMA.Plugin.Common;

/// <summary>
/// Provides helper methods for composing provider URLs and query strings.
/// </summary>
public static class PluginUriUtilities
{
    /// <summary>
    /// Adds a single encoded query parameter when the value is not empty.
    /// </summary>
    /// <param name="parameters">The query parameter collection to append to.</param>
    /// <param name="key">The query parameter name.</param>
    /// <param name="value">The query parameter value.</param>
    public static void AddQueryParameter(List<string> parameters, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        parameters.Add($"{key}={Uri.EscapeDataString(value.Trim())}");
    }

    /// <summary>
    /// Adds multiple encoded query parameters for the same key, skipping empty values.
    /// </summary>
    /// <param name="parameters">The query parameter collection to append to.</param>
    /// <param name="key">The query parameter name.</param>
    /// <param name="values">The query parameter values to append.</param>
    public static void AddQueryParameters(List<string> parameters, string key, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            parameters.Add($"{key}={Uri.EscapeDataString(value.Trim())}");
        }
    }

    /// <summary>
    /// Builds an absolute URL from a base URI, relative path, and encoded query parameters.
    /// </summary>
    /// <param name="baseUri">The base URI to build from.</param>
    /// <param name="relativePath">The relative path to append.</param>
    /// <param name="queryParameters">The encoded query parameters to append.</param>
    /// <returns>The constructed absolute URL.</returns>
    public static string BuildAbsoluteUrl(Uri baseUri, string relativePath, IReadOnlyList<string> queryParameters)
    {
        var trimmedBase = baseUri.ToString().TrimEnd('/');
        var path = relativePath.StartsWith('/') ? relativePath : $"/{relativePath}";
        if (queryParameters.Count == 0)
        {
            return $"{trimmedBase}{path}";
        }

        return $"{trimmedBase}{path}?{string.Join("&", queryParameters)}";
    }

    /// <summary>
    /// Encodes a path segment for safe inclusion in a URL.
    /// </summary>
    /// <param name="value">The raw path segment value.</param>
    /// <returns>The encoded path segment.</returns>
    public static string EncodePathSegment(string value)
    {
        return Uri.EscapeDataString(value.Trim());
    }

    /// <summary>
    /// Converts an absolute URL into its path-and-query representation.
    /// </summary>
    /// <param name="absoluteUrl">The absolute URL to convert.</param>
    /// <returns>The path and query string, or <see langword="null"/> when the URL is empty.</returns>
    public static string? ToPathAndQuery(string? absoluteUrl)
    {
        if (string.IsNullOrWhiteSpace(absoluteUrl))
        {
            return null;
        }

        return new Uri(absoluteUrl, UriKind.Absolute).PathAndQuery;
    }
}
