using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// Port for paged media chapter and page retrieval.
/// </summary>
public interface IPageProviderPort
{
    /// <summary>
    /// Gets chapter metadata for a media item.
    /// </summary>
    /// <param name="mediaId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(MediaId mediaId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a specific page within a chapter.
    /// </summary>
    /// <param name="mediaId"></param>
    /// <param name="chapterId"></param>
    /// <param name="pageIndex"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<MediaPage> GetPageAsync(MediaId mediaId, string chapterId, int pageIndex, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a batch of pages within a chapter.
    /// </summary>
    /// <param name="mediaId"></param>
    /// <param name="chapterId"></param>
    /// <param name="startIndex"></param>
    /// <param name="count"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<MediaPagesResult> GetPagesAsync(
        MediaId mediaId,
        string chapterId,
        int startIndex,
        int count,
        CancellationToken cancellationToken);
}
