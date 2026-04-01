using EMMA.Plugin.Common;

namespace EMMA.PluginTemplate.Infrastructure;

internal static class ProviderHttpProfile
{
    public static readonly PluginProviderHttpProfile Defaults = new(
    BaseUri: new Uri("https://example.invalid"),
    UserAgent: "EMMA-PluginTemplate/1.0",
        AcceptMediaType: "application/json");
}

internal static class ProviderRequestUrls
{
    private static readonly IPluginProviderUrlStrategy Strategy = PluginTemplateHooks.UrlStrategy;

    public static string? BuildSearchPath(string query)
    {
        return Strategy.BuildSearchPath(query);
    }

    public static string? BuildChaptersPath(string mediaId)
    {
        return Strategy.BuildChaptersPath(mediaId);
    }

    public static string? BuildAtHomePath(string chapterId)
    {
        return Strategy.BuildAtHomePath(chapterId);
    }

    public static string? BuildSearchAbsoluteUrl(string query)
    {
        return Strategy.BuildSearchAbsoluteUrl(query);
    }

    public static string? BuildSearchAbsoluteUrl(PluginSearchQuery query)
    {
        return Strategy.BuildSearchAbsoluteUrl(query);
    }

    public static string? BuildChaptersAbsoluteUrl(string mediaId)
    {
        return Strategy.BuildChaptersAbsoluteUrl(mediaId);
    }

    public static string? BuildAtHomeAbsoluteUrl(string chapterId)
    {
        return Strategy.BuildAtHomeAbsoluteUrl(chapterId);
    }
}