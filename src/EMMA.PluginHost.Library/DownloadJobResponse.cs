namespace EMMA.PluginHost.Library;

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
