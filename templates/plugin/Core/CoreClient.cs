using EMMA.Plugin.Common;

namespace EMMA.TemplatePlugin.Core;

internal sealed class CoreClient
{
    public IReadOnlyList<SearchItem> Search(string query)
    {
        // Replace this with provider search and map the provider response into SearchItem values.
        // Returning an empty collection keeps a freshly scaffolded plugin compilable and safe to run
        // before any provider integration exists.
        _ = query;
        return [];
    }

    public IReadOnlyList<ChapterItem> GetChapters(string mediaId)
    {
        // Replace this with chapter enumeration for the selected media item.
        _ = mediaId;
        return [];
    }

    public IReadOnlyList<ChapterOperationItem> GetChapterOperationItems(string mediaId)
    {
        // Keep this mapped from GetChapters unless your provider needs chapter-operation data that is
        // different from the chapter list surface.
        var chapters = GetChapters(mediaId);
        return chapters.Count == 0
            ? []
            :
            [
                .. chapters.Select(chapter => new ChapterOperationItem(
                    chapter.id,
                    chapter.number,
                    chapter.title,
                    chapter.uploaderGroups ?? []))
            ];
    }

    public PageItem? GetPage(string chapterId, int pageIndex)
    {
        // Replace this when your provider can resolve individual page payloads or content URLs.
        _ = chapterId;
        _ = pageIndex;
        return null;
    }

    public IReadOnlyList<PageItem> GetPages(string chapterId, int startIndex, int count)
    {
        // Replace this if your provider can fetch page windows more efficiently than repeated GetPage calls.
        _ = chapterId;
        _ = startIndex;
        _ = count;
        return [];
    }

    public int GetPageCount(string chapterId)
    {
        _ = chapterId;
        return 0;
    }
}