#if PLUGIN_TRANSPORT_WASM
using System.Text.Json;
using EMMA.Plugin.Common;
using LibraryWorld;
using LibraryWorld.wit.exports.emma.plugin;

namespace EMMA.PluginTemplate.Infrastructure;

internal sealed class WasmClient
{
    private static readonly CoreClient Core = new();

    public SearchParseMapResult SearchFromPayload(string query, string payloadJson)
    {
        var results = Core.Search(query);
        return new SearchParseMapResult(results, 0, 0);
    }

    public IReadOnlyList<ChapterItem> GetChaptersFromPayload(string mediaId, string payloadJson)
    {
        return Core.GetChapters(mediaId);
    }

    public IReadOnlyList<ChapterOperationItem> GetChapterOperationItemsFromPayload(string mediaId, string payloadJson)
    {
        var chapters = Core.GetChapters(mediaId);
        if (chapters.Count == 0)
        {
            return [];
        }

        var results = new List<ChapterOperationItem>(chapters.Count);
        foreach (var chapter in chapters)
        {
            results.Add(new ChapterOperationItem(
                chapter.id,
                chapter.number,
                chapter.title,
                chapter.uploaderGroups ?? []));
        }

        return results;
    }

    public PageItem? GetPageFromPayload(string chapterId, int pageIndex, string payloadJson)
    {
        return null;
    }

    public IReadOnlyList<PageItem> GetPagesFromPayload(
        string chapterId,
        int startIndex,
        int count,
        string payloadJson)
    {
        return [];
    }

    public string? FetchSearchPayload(string query)
    {
        return TryFetchPayload(ProviderRequestUrls.BuildSearchAbsoluteUrl(query));
    }

    public string? FetchChaptersPayload(string mediaId)
    {
        return TryFetchPayload(ProviderRequestUrls.BuildChaptersAbsoluteUrl(mediaId));
    }

    public string? FetchAtHomePayload(string chapterId)
    {
        return TryFetchPayload(ProviderRequestUrls.BuildAtHomeAbsoluteUrl(chapterId));
    }

    internal static string ResolvePayloadContent(string payload)
    {
        return PluginJsonPayload.Normalize(payload);
    }

    private static string? TryFetchPayload(string? absoluteUrl)
    {
        // TODO: Implement provider fetch when your plugin needs network-backed payload retrieval.
        return null;
    }

    public readonly record struct SearchParseMapResult(
        IReadOnlyList<SearchItem> Results,
        long ParseMs,
        long MapMs);
}
#endif
