using EMMA.Domain;

namespace EMMA.Application.Ports;

public interface IProgressPort
{
    Task<PagedMediaProgress?> GetPagedProgressAsync(
        MediaId mediaId,
        string pluginId,
        string userId,
        CancellationToken cancellationToken);

    Task SetPagedProgressAsync(
        MediaId mediaId,
        string pluginId,
        string chapterId,
        int pageIndex,
        bool completed,
        string userId,
        CancellationToken cancellationToken);

    Task<VideoMediaProgress?> GetVideoProgressAsync(
        MediaId mediaId,
        string pluginId,
        string userId,
        CancellationToken cancellationToken);

    Task SetVideoProgressAsync(
        MediaId mediaId,
        string pluginId,
        double positionSeconds,
        bool completed,
        string userId,
        CancellationToken cancellationToken);
}
