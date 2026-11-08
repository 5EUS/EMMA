namespace EMMA.Api.Contracts;

/// <summary>Sets how long history is kept.</summary>
/// <param name="Days">The number of days, 0 to 3650; 0 keeps history for ever. Older entries are deleted at once and whenever progress is recorded.</param>
public sealed record SetRetentionRequest(
    int? Days);
