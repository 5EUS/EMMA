namespace EMMA.Domain;

public sealed record DownloadJobRecord(
    string Id,
    string PluginId,
    string MediaId,
    string MediaType,
    string? ChapterId,
    string? StreamId,
    DownloadJobState State,
    int ProgressCompleted,
    int ProgressTotal,
    long BytesDownloaded,
    string? ErrorMessage,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);
