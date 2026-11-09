namespace EMMA.Api.Contracts;

/// <summary>Downloads, the most recently changed first.</summary>
/// <param name="Items">The downloads.</param>
public sealed record DownloadListResponse(
    IReadOnlyList<DownloadDto> Items);
