using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// Port for persisting and querying media metadata and chapters.
/// </summary>
public interface IMediaCatalogPort
{
    Task UpsertMediaAsync(MediaMetadata media, CancellationToken cancellationToken);
    Task<MediaMetadata?> GetMediaAsync(MediaId mediaId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaMetadata>> ListMediaAsync(int limit, CancellationToken cancellationToken);

    Task UpsertChaptersAsync(MediaId mediaId, IReadOnlyList<MediaChapterRecord> chapters, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaChapterRecord>> GetChaptersAsync(MediaId mediaId, CancellationToken cancellationToken);

    Task UpsertPagesAsync(MediaId mediaId, string chapterId, IReadOnlyList<MediaPageRecord> pages, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaPageRecord>> GetPagesAsync(MediaId mediaId, string chapterId, CancellationToken cancellationToken);
}
