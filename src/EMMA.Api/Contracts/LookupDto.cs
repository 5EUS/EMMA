namespace EMMA.Api.Contracts;

/// <summary>Completion behaviour of a lookup filter.</summary>
/// <param name="MinQueryLength">Characters required before completion starts.</param>
/// <param name="MaxResults">Most suggestions to show.</param>
/// <param name="DebounceMs">Delay before sending a completion request.</param>
/// <param name="AllowCustomValues">Whether values outside the suggestions are accepted.</param>
internal sealed record LookupDto(
    int MinQueryLength,
    int MaxResults,
    int DebounceMs,
    bool AllowCustomValues);
