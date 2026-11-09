namespace EMMA.Api.Contracts;

/// <summary>A download: one node, one asset role, and how far along it is.</summary>
/// <param name="Id">The job id.</param>
/// <param name="Ref">The node, which is everything beneath it too: a chapter, a season, a series, or a single leaf.</param>
/// <param name="Role">The asset role being stored.</param>
/// <param name="Hint">The preference passed to the plugin (a quality), when one was given.</param>
/// <param name="State"><c>queued</c>, <c>running</c>, <c>paused</c>, <c>completed</c>, <c>failed</c> or <c>canceled</c>.</param>
/// <param name="ItemsCompleted">How many leaves are stored.</param>
/// <param name="ItemsTotal">How many leaves there are; 0 until the job has walked the node.</param>
/// <param name="BytesDownloaded">The size of what is stored.</param>
/// <param name="Error">Why the job failed, when it did.</param>
/// <param name="CreatedAt">When the job was created.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="StartedAt">When it first started running.</param>
/// <param name="CompletedAt">When it reached a final state.</param>
public sealed record DownloadDto(
    string Id,
    string Ref,
    string Role,
    string? Hint,
    string State,
    int ItemsCompleted,
    int ItemsTotal,
    long BytesDownloaded,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);
