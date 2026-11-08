namespace EMMA.Domain;

/// <summary>
/// A download of a node's assets for one role. One executor serves every media type:
/// it walks <c>children</c> to the nodes whose kind declares the role and resolves each.
/// </summary>
/// <param name="Id">The job id.</param>
/// <param name="Node">The node to download (everything beneath it is included).</param>
/// <param name="Role">The asset role to download.</param>
/// <param name="State">The lifecycle state.</param>
/// <param name="ItemsCompleted">How many leaf nodes are done.</param>
/// <param name="ItemsTotal">How many leaf nodes there are, once known.</param>
/// <param name="BytesDownloaded">Bytes stored so far.</param>
/// <param name="ErrorMessage">The failure message when <paramref name="State"/> is failed.</param>
/// <param name="CreatedAt">When the job was created.</param>
/// <param name="UpdatedAt">When the job last changed.</param>
/// <param name="StartedAt">When it started running.</param>
/// <param name="CompletedAt">When it reached a final state.</param>
public sealed record DownloadJob(
    Guid Id,
    NodeRef Node,
    AssetRole Role,
    DownloadState State,
    int ItemsCompleted,
    int ItemsTotal,
    long BytesDownloaded,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);
