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

        // Replace the parse/map timing placeholders once the WASM transport needs explicit payload
        // deserialization or richer provider response mapping.
        return new SearchParseMapResult(Core.Search(query), 0, 0);
    }

    public IReadOnlyList<ChapterItem> GetChaptersFromPayload(string mediaId, string payloadJson)
    {
        // Use payloadJson when the provider requires a fetch-at-home or chapter bootstrap payload.
        _ = payloadJson;
        return Core.GetChapters(mediaId);
    }

    public IReadOnlyList<ChapterOperationItem> GetChapterOperationItemsFromPayload(string mediaId, string payloadJson)
    {
        _ = payloadJson;
        return Core.GetChapterOperationItems(mediaId);
    }

    public PageItem? GetPageFromPayload(string chapterId, int pageIndex, string payloadJson)
    {
        _ = payloadJson;
        return Core.GetPage(chapterId, pageIndex);
    }

    public IReadOnlyList<PageItem> GetPagesFromPayload(string chapterId, int startIndex, int count, string payloadJson)
    {
        _ = payloadJson;
        return Core.GetPages(chapterId, startIndex, count);
    }

    public string? FetchSearchPayload(PluginSearchQuery query)
        // Keep this as the minimal query payload until you need provider-specific request metadata.
        => JsonSerializer.Serialize(new SearchRequest(query.Query));

    public string? FetchChaptersPayload(string mediaId)
    {
        // Return serialized chapter bootstrap input here when the provider requires it.
        _ = mediaId;
        return null;
    }

    public string? FetchAtHomePayload(string chapterId)
    {
        // Return serialized page bootstrap input here when page fetching needs an upstream token or URL.
        _ = chapterId;
        return null;
    }

    internal sealed record SearchRequest(string Query);

    public readonly record struct SearchParseMapResult(
        IReadOnlyList<SearchItem> Results,
        long ParseMs,
        long MapMs);
}