namespace EMMA.Api.Contracts;

/// <summary>Recent progress.</summary>
/// <param name="Items">Most recent first.</param>
public sealed record ProgressListResponse(
    IReadOnlyList<ProgressDto> Items);
