namespace EMMA.Api.Contracts;

/// <summary>Result counts for one filter.</summary>
/// <param name="FilterId">The filter.</param>
/// <param name="Counts">The counts.</param>
internal sealed record FacetDto(
    string FilterId,
    IReadOnlyList<FacetCountDto> Counts);
