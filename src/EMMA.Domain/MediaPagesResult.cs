namespace EMMA.Domain;

/// <summary>
/// Batched page retrieval result for a chapter.
/// </summary>
public sealed record MediaPagesResult(
    IReadOnlyList<MediaPage> Pages,
    bool ReachedEnd);
