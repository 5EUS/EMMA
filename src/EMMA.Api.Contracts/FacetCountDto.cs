namespace EMMA.Api.Contracts;

/// <summary>The number of results for one filter value.</summary>
/// <param name="Value">The filter value.</param>
/// <param name="Label">The display label.</param>
/// <param name="Count">How many results match.</param>
public sealed record FacetCountDto(
    string Value,
    string Label,
    long Count);
