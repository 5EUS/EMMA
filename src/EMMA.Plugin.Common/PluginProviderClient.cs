namespace EMMA.Plugin.Common;

/// <summary>
/// Generic base class for provider HTTP client configuration and URL building.
/// Consolidates HTTP profile (base URI, headers) with URL builder patterns.
/// </summary>
public abstract class PluginProviderClient
{
    /// <summary>
    /// Provider HTTP profile (base URI, user agent, etc.).
    /// </summary>
    protected abstract PluginProviderHttpProfile HttpProfile { get; }

    /// <summary>
    /// Base absolute URL for the provider (from HttpProfile).
    /// </summary>
    public Uri BaseUri => HttpProfile.BaseUri;

    /// <summary>
    /// User agent header value.
    /// </summary>
    public string UserAgent => HttpProfile.UserAgent;

    /// <summary>
    /// Accept media type header value.
    /// </summary>
    public string AcceptMediaType => HttpProfile.AcceptMediaType;

    /// <summary>
    /// Convert absolute URL to path+query string for use with HttpClient.
    /// </summary>
    protected static string? AbsoluteUrlToPath(string? absoluteUrl)
    {
        if (string.IsNullOrWhiteSpace(absoluteUrl))
        {
            return null;
        }

        return PluginUriUtilities.ToPathAndQuery(absoluteUrl);
    }

    /// <summary>
    /// Build absolute URL from base URI, path template, and query parameters.
    /// </summary>
    protected string? BuildAbsoluteUrl(string pathTemplate, IReadOnlyList<string> queryParameters)
    {
        return PluginUriUtilities.BuildAbsoluteUrl(BaseUri, pathTemplate, queryParameters);
    }

    /// <summary>
    /// Build absolute URL for resource by ID (common pattern).
    /// </summary>
    protected string? BuildResourceAbsoluteUrl(string pathTemplate, string resourceId, IReadOnlyList<string> queryParameters)
    {
        return PluginProviderUrlTemplates.BuildResourceByIdAbsoluteUrl(
            BaseUri,
            pathTemplate,
            resourceId,
            queryParameters);
    }

    /// <summary>
    /// Build path+query from path template and resource ID.
    /// </summary>
    protected string? BuildResourcePath(string pathTemplate, string resourceId, IReadOnlyList<string> queryParameters)
    {
        var absoluteUrl = BuildResourceAbsoluteUrl(pathTemplate, resourceId, queryParameters);
        return AbsoluteUrlToPath(absoluteUrl);
    }

    /// <summary>
    /// Parse filter values from a search query.
    /// </summary>
    protected static IReadOnlyList<string> GetFilterValues(PluginSearchQuery query, string filterId)
    {
        return query.GetFilterValues(filterId);
    }

    /// <summary>
    /// Parse additional values from a search query.
    /// </summary>
    protected static string? GetQueryAddition(PluginSearchQuery query, string key)
    {
        return query.GetQueryAddition(key);
    }
}
