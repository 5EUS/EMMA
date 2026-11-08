namespace EMMA.Api.Contracts;

/// <summary>How long the caller's history is kept.</summary>
/// <param name="Days">The number of days; 0 keeps history for ever.</param>
/// <param name="Removed">How many entries applying a new setting deleted; absent when only reading.</param>
public sealed record RetentionResponse(
    int Days,
    int? Removed);
