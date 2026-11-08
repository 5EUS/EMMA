namespace EMMA.Api.Contracts;

/// <summary>Recent activity grouped by series.</summary>
/// <param name="Groups">The series with the latest activity first.</param>
public sealed record ProgressHistoryResponse(
    IReadOnlyList<ProgressGroupDto> Groups);
