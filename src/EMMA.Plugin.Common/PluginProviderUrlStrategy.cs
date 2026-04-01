namespace EMMA.Plugin.Common;

public interface IPluginProviderUrlStrategy
{
    string? BuildSearchAbsoluteUrl(PluginSearchQuery query);

    string? BuildChaptersAbsoluteUrl(string mediaId);

    string? BuildAtHomeAbsoluteUrl(string chapterId);
}

public static class PluginProviderUrlStrategyExtensions
{
    public static string? BuildSearchAbsoluteUrl(this IPluginProviderUrlStrategy strategy, string query)
    {
        return strategy.BuildSearchAbsoluteUrl(new PluginSearchQuery(query ?? string.Empty, [], [], [], null, null, null));
    }

    public static string? BuildSearchPath(this IPluginProviderUrlStrategy strategy, string query)
    {
        return PluginUriUtilities.ToPathAndQuery(strategy.BuildSearchAbsoluteUrl(query));
    }

    public static string? BuildChaptersPath(this IPluginProviderUrlStrategy strategy, string mediaId)
    {
        return PluginUriUtilities.ToPathAndQuery(strategy.BuildChaptersAbsoluteUrl(mediaId));
    }

    public static string? BuildAtHomePath(this IPluginProviderUrlStrategy strategy, string chapterId)
    {
        return PluginUriUtilities.ToPathAndQuery(strategy.BuildAtHomeAbsoluteUrl(chapterId));
    }
}