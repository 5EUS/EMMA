using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// Port for read/watch history entries.
/// </summary>
public interface IHistoryPort
{
    Task UpsertAsync(MediaHistoryEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaHistoryEntry>> GetHistoryAsync(string userId, int limit, CancellationToken cancellationToken);
    Task DeleteForMediaAsync(MediaId mediaId, string pluginId, string userId, CancellationToken cancellationToken);
}
