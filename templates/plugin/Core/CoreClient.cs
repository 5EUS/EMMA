using EMMA.Plugin.Common;

namespace EMMA.TemplatePlugin.Core;

internal sealed class CoreClient
{
    private static readonly SearchItem[] SearchCatalog =
    [
        new(
            id: "template-series-1",
            source: "emma.plugin.template",
            title: "Template Series One",
            mediaType: PluginMediaTypes.Paged,
            thumbnailUrl: "https://example.invalid/template/series-1/poster.jpg",
            description: "A deterministic paged fixture with two chapters."),
        new(
            id: "template-collection-1",
            source: "emma.plugin.template",
            title: "Template Collection One",
            mediaType: PluginMediaTypes.Paged,
            thumbnailUrl: "https://example.invalid/template/collection-1/poster.jpg",
            description: "A second deterministic fixture for validating search and paging.")
    ];

    private static readonly IReadOnlyDictionary<string, ChapterItem[]> ChaptersByMediaId =
        new Dictionary<string, ChapterItem[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["template-series-1"] =
            [
                new ChapterItem("template-series-1-chapter-1", 1, "Chapter 1", []),
                new ChapterItem("template-series-1-chapter-2", 2, "Chapter 2", [])
            ],
            ["template-collection-1"] =
            [
                new ChapterItem("template-collection-1-chapter-1", 1, "Issue 1", [])
            ]
        };

    private static readonly IReadOnlyDictionary<string, PageItem[]> PagesByChapterId =
        new Dictionary<string, PageItem[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["template-series-1-chapter-1"] = BuildPages("template-series-1-chapter-1", 4),
            ["template-series-1-chapter-2"] = BuildPages("template-series-1-chapter-2", 3),
            ["template-collection-1-chapter-1"] = BuildPages("template-collection-1-chapter-1", 5)
        };

    public IReadOnlyList<SearchItem> Search(string query)
    {
        var normalized = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return SearchCatalog;
        }

        var matches = SearchCatalog
            .Where(item =>
                item.id.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || item.title.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || (item.description?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToArray();

        return matches.Length == 0 ? SearchCatalog : matches;
    }

    public IReadOnlyList<ChapterItem> GetChapters(string mediaId)
    {
        return ChaptersByMediaId.TryGetValue(mediaId, out var chapters)
            ? chapters
            : [];
    }

    public IReadOnlyList<ChapterOperationItem> GetChapterOperationItems(string mediaId)
    {
        var chapters = GetChapters(mediaId);
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

    public PageItem? GetPage(string chapterId, int pageIndex)
    {
        if (pageIndex < 0)
        {
            return null;
        }

        return PagesByChapterId.TryGetValue(chapterId, out var pages) && pageIndex < pages.Length
            ? pages[pageIndex]
            : null;
    }

    public IReadOnlyList<PageItem> GetPages(string chapterId, int startIndex, int count)
    {
        if (startIndex < 0 || count <= 0 || !PagesByChapterId.TryGetValue(chapterId, out var pages) || startIndex >= pages.Length)
        {
            return [];
        }

        var max = Math.Min(count, pages.Length - startIndex);
        return pages.Skip(startIndex).Take(max).ToArray();
    }

    public int GetPageCount(string chapterId)
    {
        return PagesByChapterId.TryGetValue(chapterId, out var pages)
            ? pages.Length
            : 0;
    }

    private static PageItem[] BuildPages(string chapterId, int count)
    {
        var pages = new PageItem[count];
        for (var index = 0; index < count; index++)
        {
            pages[index] = new PageItem(
                $"{chapterId}:page:{index}",
                index,
                $"https://example.invalid/assets/{chapterId}/page-{index + 1}.jpg");
        }

        return pages;
    }
}