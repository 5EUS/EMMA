namespace EMMA.Domain;

/// <summary>
/// Persisted page metadata for paged media.
/// </summary>
public sealed record MediaPageRecord(
    string PageId,
    MediaId MediaId,
    string ChapterId,
    int Index,
    string ContentUri,
    DateTimeOffset UpdatedAtUtc);
