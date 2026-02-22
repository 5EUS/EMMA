namespace EMMA.Domain;

/// <summary>
/// Persisted metadata for a media item.
/// </summary>
public sealed record MediaMetadata(
    MediaId Id,
    string SourceId,
    string Title,
    MediaType MediaType,
    string? Rating,
    string? Synopsis,
    string? Language,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
