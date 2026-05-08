namespace EMMA.PluginHost.Library;

/// <summary>
/// Represents the public state of a download job.
/// </summary>
/// <param name="Id">The download job identifier.</param>
/// <param name="PluginId">The plugin that owns the download.</param>
/// <param name="MediaId">The media identifier being downloaded.</param>
/// <param name="MediaType">The media type being downloaded.</param>
/// <param name="ChapterId">The optional chapter identifier.</param>
/// <param name="StreamId">The optional stream identifier.</param>
/// <param name="State">The current download state.</param>
/// <param name="ProgressCompleted">The number of completed work units.</param>
/// <param name="ProgressTotal">The total number of work units.</param>
/// <param name="BytesDownloaded">The total downloaded bytes.</param>
/// <param name="ErrorMessage">The optional failure message.</param>
/// <param name="CreatedAtUtc">The creation timestamp in UTC.</param>
/// <param name="UpdatedAtUtc">The last update timestamp in UTC.</param>
/// <param name="StartedAtUtc">The optional start timestamp in UTC.</param>
/// <param name="CompletedAtUtc">The optional completion timestamp in UTC.</param>
public sealed record DownloadJobResponse(
    string Id,
    string PluginId,
    string MediaId,
    string MediaType,
    string? ChapterId,
    string? StreamId,
    string State,
    int ProgressCompleted,
    int ProgressTotal,
    long BytesDownloaded,
    string? ErrorMessage,
    string CreatedAtUtc,
    string UpdatedAtUtc,
    string? StartedAtUtc,
    string? CompletedAtUtc);
