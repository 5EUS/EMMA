using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.InMemory;

/// <summary>
/// In-memory page provider adapter backed by the demo store.
/// </summary>
public sealed class InMemoryPageProvider(InMemoryMediaStore store) : IPageProviderPort
{
    private readonly InMemoryMediaStore _store = store;

    /// <summary>
    /// Gets chapter metadata from the in-memory store.
    /// </summary>
    public Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(MediaId mediaId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.GetChapters(mediaId));
    }

    /// <summary>
    /// Gets a page from the in-memory store.
    /// </summary>
    public Task<MediaPage> GetPageAsync(
        MediaId mediaId,
        string chapterId,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var page = _store.GetPage(mediaId, chapterId, pageIndex) ?? throw new KeyNotFoundException($"Page {pageIndex} not found for chapter {chapterId}.");
        return Task.FromResult(page);
    }

    /// <summary>
    /// Gets a page batch from the in-memory store.
    /// </summary>
    public Task<MediaPagesResult> GetPagesAsync(
        MediaId mediaId,
        string chapterId,
        int startIndex,
        int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (startIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        }

        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var pages = new List<MediaPage>(count);
        var reachedEnd = false;

        for (var offset = 0; offset < count; offset++)
        {
            var index = startIndex + offset;
            var page = _store.GetPage(mediaId, chapterId, index);
            if (page is null)
            {
                reachedEnd = true;
                break;
            }

            pages.Add(page);
        }

        return Task.FromResult(new MediaPagesResult(pages, reachedEnd));
    }
}
