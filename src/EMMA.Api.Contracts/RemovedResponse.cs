namespace EMMA.Api.Contracts;

/// <summary>What a delete removed.</summary>
/// <param name="Removed">How many entries were deleted; zero is not an error.</param>
public sealed record RemovedResponse(
    int Removed);
