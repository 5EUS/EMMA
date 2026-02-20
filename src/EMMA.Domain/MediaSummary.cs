namespace EMMA.Domain;

/// <summary>
/// Minimal summary returned from search results.
/// </summary>
public sealed record MediaSummary(
    MediaId Id,
    string SourceId,
    string Title,
    MediaType MediaType);
