using EMMA.Domain;

namespace EMMA.Infrastructure.InMemory;

/// <summary>
/// In-memory store for demo media, chapters, and pages.
/// </summary>
public sealed class InMemoryMediaStore
{
    private readonly Dictionary<MediaId, MediaSummary> _media = [];
    private readonly Dictionary<MediaId, List<MediaChapter>> _chapters = [];
    private readonly Dictionary<(MediaId MediaId, string ChapterId, int PageIndex), MediaPage> _pages = [];

    public IReadOnlyCollection<MediaSummary> Media => _media.Values;

    /// <summary>
    /// Adds or replaces a media summary.
    /// </summary>
    public void AddMedia(MediaSummary summary)
    {
        _media[summary.Id] = summary;
    }

    /// <summary>
    /// Adds a chapter for a media item.
    /// </summary>
    public void AddChapter(MediaId mediaId, MediaChapter chapter)
    {
        if (!_chapters.TryGetValue(mediaId, out var list))
        {
            list = [];
            _chapters[mediaId] = list;
        }

        list.Add(chapter);
    }

    /// <summary>
    /// Adds a page for a chapter.
    /// </summary>
    public void AddPage(MediaId mediaId, string chapterId, MediaPage page)
    {
        _pages[(mediaId, chapterId, page.Index)] = page;
    }

    /// <summary>
    /// Gets chapters for a media item.
    /// </summary>
    public IReadOnlyList<MediaChapter> GetChapters(MediaId mediaId)
    {
        return _chapters.TryGetValue(mediaId, out var list)
            ? list
            : Array.Empty<MediaChapter>();
    }

    /// <summary>
    /// Gets a specific page or null if missing.
    /// </summary>
    public MediaPage? GetPage(MediaId mediaId, string chapterId, int pageIndex)
    {
        return _pages.TryGetValue((mediaId, chapterId, pageIndex), out var page)
            ? page
            : null;
    }
}
