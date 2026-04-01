using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EMMA.Plugin.Common;

public static class PluginWasmPagingJsonHelpers
{
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
}
