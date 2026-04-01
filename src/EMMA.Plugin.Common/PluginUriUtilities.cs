namespace EMMA.Plugin.Common;

public static class PluginUriUtilities
{
    public static void AddQueryParameter(List<string> parameters, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        parameters.Add($"{key}={Uri.EscapeDataString(value.Trim())}");
    }

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

    public static string BuildAbsoluteUrl(Uri baseUri, string relativePath, IReadOnlyList<string> queryParameters)
    {
        var path = relativePath.StartsWith('/') ? relativePath : $"/{relativePath}";
        if (queryParameters.Count == 0)
        {
            return $"{baseUri}{path}";
        }

        return $"{baseUri}{path}?{string.Join("&", queryParameters)}";
    }

    public static string EncodePathSegment(string value)
    {
        return Uri.EscapeDataString(value.Trim());
    }

    public static string? ToPathAndQuery(string? absoluteUrl)
    {
        if (string.IsNullOrWhiteSpace(absoluteUrl))
        {
            return null;
        }

        return new Uri(absoluteUrl, UriKind.Absolute).PathAndQuery;
    }
}
