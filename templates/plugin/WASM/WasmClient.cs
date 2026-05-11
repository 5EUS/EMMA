using System.Text.Json;
using EMMA.Plugin.Common;
using EMMA.TemplatePlugin.Core;

namespace EMMA.TemplatePlugin.WASM;

internal sealed class WasmClient
{
    private static readonly CoreClient Core = new();

    public SearchParseMapResult SearchFromPayloadWithTimings(string payloadJson)
    {
        var query = string.Empty;
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                query = JsonSerializer.Deserialize<SearchRequest>(payloadJson)?.Query ?? string.Empty;
            }
            catch (JsonException)
            {
                query = string.Empty;
            }
        }

        return new SearchParseMapResult(Core.Search(query), 0, 0);
    }

    public IReadOnlyList<ChapterItem> GetChaptersFromPayload(string mediaId, string payloadJson)
        => Core.GetChapters(mediaId);

    public IReadOnlyList<ChapterOperationItem> GetChapterOperationItemsFromPayload(string mediaId, string payloadJson)
        => Core.GetChapterOperationItems(mediaId);

    public PageItem? GetPageFromPayload(string chapterId, int pageIndex, string payloadJson)
        => Core.GetPage(chapterId, pageIndex);

    public IReadOnlyList<PageItem> GetPagesFromPayload(string chapterId, int startIndex, int count, string payloadJson)
        => Core.GetPages(chapterId, startIndex, count);

    public string? FetchSearchPayload(PluginSearchQuery query)
        => JsonSerializer.Serialize(new SearchRequest(query.Query));

    public string? FetchChaptersPayload(string mediaId)
        => null;

    public string? FetchAtHomePayload(string chapterId)
        => null;

    internal sealed record SearchRequest(string Query);

    public readonly record struct SearchParseMapResult(
        IReadOnlyList<SearchItem> Results,
        long ParseMs,
        long MapMs);
}