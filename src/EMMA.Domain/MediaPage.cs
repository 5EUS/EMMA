namespace EMMA.Domain;

/// <summary>
/// Page metadata for paged media.
/// </summary>
public sealed record MediaPage(
    string PageId,
    int Index,
    Uri ContentUri);
