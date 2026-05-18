namespace EMMA.Plugin.Common;

/// <summary>
/// Defines provider-specific URL construction behavior.
/// </summary>
public interface IPluginProviderUrlStrategy
{
    /// <summary>
    /// Builds the absolute search URL for a parsed query.
    /// </summary>
    /// <param name="query">The parsed search query to translate into a provider URL.</param>
    /// <returns>The absolute search URL, or <see langword="null"/> when the query cannot be expressed.</returns>
    string? BuildSearchAbsoluteUrl(PluginSearchQuery query);

    /// <summary>
    /// Builds the absolute chapters URL for a media identifier.
    /// </summary>
    /// <param name="mediaId">The media identifier to resolve.</param>
    /// <returns>The absolute chapters URL, or <see langword="null"/> when the identifier is invalid.</returns>
    string? BuildChaptersAbsoluteUrl(string mediaId);

    /// <summary>
    /// Builds the absolute at-home URL for a chapter identifier.
    /// </summary>
    /// <param name="chapterId">The chapter identifier to resolve.</param>
    /// <returns>The absolute at-home URL, or <see langword="null"/> when the identifier is invalid.</returns>
    string? BuildAtHomeAbsoluteUrl(string chapterId);
}

/// <summary>
/// Adds convenience overloads to <see cref="IPluginProviderUrlStrategy"/>.
/// </summary>
public static class PluginProviderUrlStrategyExtensions
{
    /// <summary>
    /// Builds the absolute search URL for a raw query string.
    /// </summary>
    /// <param name="strategy">The URL strategy to use.</param>
    /// <param name="query">The raw search query text.</param>
    /// <returns>The absolute search URL, or <see langword="null"/> when the query cannot be expressed.</returns>
    public static string? BuildSearchAbsoluteUrl(this IPluginProviderUrlStrategy strategy, string query)
    {
        return strategy.BuildSearchAbsoluteUrl(new PluginSearchQuery(query ?? string.Empty, [], [], [], null, null, null));
    }

    /// <summary>
    /// Builds the search path and query string for a raw query string.
    /// </summary>
    /// <param name="strategy">The URL strategy to use.</param>
    /// <param name="query">The raw search query text.</param>
    /// <returns>The provider path and query string, or <see langword="null"/> when the search URL cannot be built.</returns>
    public static string? BuildSearchPath(this IPluginProviderUrlStrategy strategy, string query)
    {
        return PluginUriUtilities.ToPathAndQuery(strategy.BuildSearchAbsoluteUrl(query));
    }

    /// <summary>
    /// Builds the chapters path and query string for a media identifier.
    /// </summary>
    /// <param name="strategy">The URL strategy to use.</param>
    /// <param name="mediaId">The media identifier to resolve.</param>
    /// <returns>The provider path and query string, or <see langword="null"/> when the chapters URL cannot be built.</returns>
    public static string? BuildChaptersPath(this IPluginProviderUrlStrategy strategy, string mediaId)
    {
        return PluginUriUtilities.ToPathAndQuery(strategy.BuildChaptersAbsoluteUrl(mediaId));
    }

    /// <summary>
    /// Builds the at-home path and query string for a chapter identifier.
    /// </summary>
    /// <param name="strategy">The URL strategy to use.</param>
    /// <param name="chapterId">The chapter identifier to resolve.</param>
    /// <returns>The provider path and query string, or <see langword="null"/> when the at-home URL cannot be built.</returns>
    public static string? BuildAtHomePath(this IPluginProviderUrlStrategy strategy, string chapterId)
    {
        return PluginUriUtilities.ToPathAndQuery(strategy.BuildAtHomeAbsoluteUrl(chapterId));
    }
}