using EMMA.Plugin.Common;

namespace EMMA.PluginTemplate.Infrastructure;

/// <summary>
/// Plugin domain implementation shared by ASP.NET and WASM transports.
/// </summary>
internal sealed class CoreClient
{
    public IReadOnlyList<SearchItem> Search(string query)
    {
        return PluginTemplateHooks.Search(query);
    }

    public IReadOnlyList<ChapterItem> GetChapters(string mediaId)
    {
        return PluginTemplateHooks.GetChapters(mediaId);
    }

    public IReadOnlyList<PluginTemplateVideoStream> GetStreams(string mediaId)
    {
        return PluginTemplateHooks.GetStreams(mediaId);
    }

    public PluginTemplateVideoSegment? GetSegment(string mediaId, string streamId, uint sequence)
    {
        return PluginTemplateHooks.GetSegment(mediaId, streamId, sequence);
    }
}