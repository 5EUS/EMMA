namespace EMMA.Api.Contracts;

/// <summary>One leaf of a download.</summary>
/// <param name="Ref">The leaf node. Reading its asset (<c>role</c> of the download, index 0) is served from the stored file.</param>
/// <param name="Position">Where it comes in the download, in the order the plugin lists the leaves.</param>
/// <param name="Stored">Whether the asset is stored.</param>
/// <param name="Mime">The type of the stored asset.</param>
/// <param name="Bytes">The size of the stored asset; 0 while it is not stored.</param>
/// <param name="Error">Why the last attempt failed, when it did.</param>
public sealed record DownloadItemDto(
    string Ref,
    int Position,
    bool Stored,
    string? Mime,
    long Bytes,
    string? Error);
