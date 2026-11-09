namespace EMMA.Api.Contracts;

/// <summary>A download and its leaves.</summary>
/// <param name="Download">The download.</param>
/// <param name="Items">The leaves in order; empty until the download has walked its node.</param>
public sealed record DownloadDetailResponse(
    DownloadDto Download,
    IReadOnlyList<DownloadItemDto> Items);
