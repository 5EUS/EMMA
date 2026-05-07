using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EMMA.Plugin.Common;

public static class PluginWasmPagingJsonHelpers
{
    public static string MergeChapterFeedPages(
        string firstPayload,
        int maxPages,
        Func<int, string?> fetchNextPayload)
    {
        if (string.IsNullOrWhiteSpace(firstPayload))
        {
            return string.Empty;
        }

        if (!TryGetChapterFeedPageStats(firstPayload, out var firstStats))
        {
            return firstPayload;
        }

        var dataEntries = new List<string>();
        var includedEntries = new List<string>();
        var seenChapterIds = new HashSet<string>(StringComparer.Ordinal);
        var seenIncludedKeys = new HashSet<string>(StringComparer.Ordinal);

        AppendChapterFeedPage(firstPayload, dataEntries, includedEntries, seenChapterIds, seenIncludedKeys);

        var pagesFetched = 1;
        var nextOffset = firstStats.Offset + firstStats.DataCount;

        while (pagesFetched < maxPages && nextOffset < firstStats.Total)
        {
            var nextPayload = fetchNextPayload(nextOffset);
            if (string.IsNullOrWhiteSpace(nextPayload))
            {
                break;
            }

            AppendChapterFeedPage(nextPayload, dataEntries, includedEntries, seenChapterIds, seenIncludedKeys);

            if (!TryGetChapterFeedPageStats(nextPayload, out var nextStats) || nextStats.DataCount <= 0)
            {
                break;
            }

            nextOffset = nextStats.Offset + nextStats.DataCount;
            pagesFetched++;
        }

        return BuildMergedChapterPayload(dataEntries, includedEntries);
    }

    public static string SerializePageForCli(
        string[] args,
        string stdinPayload,
        Func<string, string, uint, string, PageItem?> pageHandler,
        JsonTypeInfo<PageItem> pageTypeInfo)
    {
        if (args.Length < 3)
        {
            return string.Empty;
        }

        var mediaId = args[0];
        var chapterId = args[1];
        if (!uint.TryParse(args[2], out var pageIndex))
        {
            return string.Empty;
        }

        var result = pageHandler(mediaId, chapterId, pageIndex, stdinPayload);
        if (result is null)
        {
            return "null";
        }

        return JsonSerializer.Serialize(result, pageTypeInfo);
    }

    public static string SerializePagesForCli(
        string[] args,
        string stdinPayload,
        Func<string, string, uint, uint, string, PageItem[]> pagesHandler,
        JsonTypeInfo<PageItem[]> pageArrayTypeInfo)
    {
        if (args.Length < 4)
        {
            return string.Empty;
        }

        var mediaId = args[0];
        var chapterId = args[1];
        if (!uint.TryParse(args[2], out var startIndex)
            || !uint.TryParse(args[3], out var count)
            || count == 0)
        {
            return string.Empty;
        }

        var results = pagesHandler(mediaId, chapterId, startIndex, count, stdinPayload);
        return JsonSerializer.Serialize(results, pageArrayTypeInfo);
    }

    public static IReadOnlyList<TWire> MapChapterOperationItems<TWire>(
        IReadOnlyList<ChapterOperationItem> items,
        Func<ChapterOperationItem, TWire> mapper)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var result = new List<TWire>(items.Count);
        foreach (var item in items)
        {
            result.Add(mapper(item));
        }

        return result;
    }

    private static void AppendChapterFeedPage(
        string payloadJson,
        List<string> dataEntries,
        List<string> includedEntries,
        HashSet<string> seenChapterIds,
        HashSet<string> seenIncludedKeys)
    {
        var normalized = PluginJsonPayload.Normalize(payloadJson);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        using var doc = JsonDocument.Parse(normalized);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var chapterId = item.TryGetProperty("id", out var idElement)
                    ? idElement.GetString() ?? string.Empty
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(chapterId) || !seenChapterIds.Add(chapterId))
                {
                    continue;
                }

                dataEntries.Add(item.GetRawText());
            }
        }

        if (root.TryGetProperty("included", out var included) && included.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in included.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = item.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString() ?? string.Empty
                    : string.Empty;
                var id = item.TryGetProperty("id", out var idElement)
                    ? idElement.GetString() ?? string.Empty
                    : string.Empty;
                var key = string.IsNullOrWhiteSpace(type) && string.IsNullOrWhiteSpace(id)
                    ? item.GetRawText()
                    : $"{type}:{id}";

                if (!seenIncludedKeys.Add(key))
                {
                    continue;
                }

                includedEntries.Add(item.GetRawText());
            }
        }
    }

    private static bool TryGetChapterFeedPageStats(string payloadJson, out ChapterFeedPageStats stats)
    {
        stats = default;

        var normalized = PluginJsonPayload.Normalize(payloadJson);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        using var doc = JsonDocument.Parse(normalized);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var dataCount = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.GetArrayLength()
            : 0;

        var total = root.TryGetProperty("total", out var totalElement) && totalElement.TryGetInt32(out var totalValue)
            ? totalValue
            : dataCount;

        var offset = root.TryGetProperty("offset", out var offsetElement) && offsetElement.TryGetInt32(out var offsetValue)
            ? offsetValue
            : 0;

        stats = new ChapterFeedPageStats(dataCount, total, offset);
        return true;
    }

    private static string BuildMergedChapterPayload(List<string> dataEntries, List<string> includedEntries)
    {
        var dataJson = string.Join(',', dataEntries);
        var includedJson = string.Join(',', includedEntries);
        return $"{{\"data\":[{dataJson}],\"included\":[{includedJson}]}}";
    }

    private readonly record struct ChapterFeedPageStats(int DataCount, int Total, int Offset);
}
