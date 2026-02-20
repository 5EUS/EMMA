using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// Port for searching media summaries across a source.
/// </summary>
public interface IMediaSearchPort
{
    /// <summary>
    /// Searches for media summaries matching the query.
    /// </summary>
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IReadOnlyList<MediaSummary>> SearchAsync(string query, CancellationToken cancellationToken);
}
