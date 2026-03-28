namespace EMMA.Domain;

/// <summary>
/// Chapter metadata for paged media.
/// </summary>
public sealed record MediaChapter(
    string ChapterId,
    int Number,
    string Title,
    IReadOnlyList<string>? UploaderGroups = null);
