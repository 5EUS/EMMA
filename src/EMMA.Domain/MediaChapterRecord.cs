namespace EMMA.Domain;

/// <summary>
/// Persisted chapter metadata for a media item.
/// </summary>
public sealed record MediaChapterRecord(
    string ChapterId,
    MediaId MediaId,
    int Number,
    string Title,
    DateTimeOffset? PublishedAtUtc,
    IReadOnlyList<string>? UploaderGroups = null);
