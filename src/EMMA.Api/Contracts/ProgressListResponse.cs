namespace EMMA.Api.Contracts;

/// <summary>Recent progress.</summary>
/// <param name="Items">Most recent first.</param>
internal sealed record ProgressListResponse(
    IReadOnlyList<ProgressDto> Items);
