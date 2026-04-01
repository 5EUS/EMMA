using EMMA.Plugin.Common;

namespace EMMA.PluginTemplate.Infrastructure;

internal sealed record PluginTemplateVideoStream(
    string Id,
    string Label,
    string PlaylistUri);

internal sealed record PluginTemplateVideoSegment(
    string ContentType,
    byte[] Payload);

internal static class PluginTemplateHooks
{
    public static readonly IPluginProviderUrlStrategy UrlStrategy = new TemplateUrlStrategy();

    public static IReadOnlyList<SearchItem> Search(string query)
    {
        // TODO: Return real search results from your provider or local store.
        return [];
    }

    public static IReadOnlyList<ChapterItem> GetChapters(string mediaId)
    {
        // TODO: Return chapter/page-group navigation entries for a media id.
        return [];
    }

    public static IReadOnlyList<PluginTemplateVideoStream> GetStreams(string mediaId)
    {
        // TODO: Return available video streams for a media id.
        return [];
    }

    public static PluginTemplateVideoSegment? GetSegment(string mediaId, string streamId, uint sequence)
    {
        // TODO: Return binary segment payload for chunked streaming implementations.
        return null;
    }

    private sealed class TemplateUrlStrategy : IPluginProviderUrlStrategy
    {
        public string? BuildSearchAbsoluteUrl(PluginSearchQuery query)
        {
            // TODO: Build and return provider-specific search URL, or keep null for local-only plugins.
            return null;
        }

        public string? BuildChaptersAbsoluteUrl(string mediaId)
        {
            // TODO: Build and return provider-specific chapters URL.
            return null;
        }

        public string? BuildAtHomeAbsoluteUrl(string chapterId)
        {
            // TODO: Build and return provider-specific page/asset URL.
            return null;
        }
    }
}