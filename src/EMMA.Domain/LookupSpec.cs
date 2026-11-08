namespace EMMA.Domain;

/// <summary>Completion behaviour of a <see cref="FilterType.Lookup"/> filter.</summary>
/// <param name="MinQueryLength">Characters required before completion starts.</param>
/// <param name="MaxResults">Maximum suggestions to show.</param>
/// <param name="DebounceMs">Delay before sending a completion request.</param>
/// <param name="AllowCustomValues">Whether values outside the suggestions are accepted.</param>
public sealed record LookupSpec(int MinQueryLength, int MaxResults, int DebounceMs, bool AllowCustomValues);
