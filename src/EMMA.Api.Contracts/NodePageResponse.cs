namespace EMMA.Api.Contracts;

/// <summary>One page of nodes.</summary>
/// <param name="Items">The nodes, in order.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> for the next page; absent on the last page.</param>
/// <param name="Total">The known total.</param>
/// <param name="Facets">Result counts per filter value.</param>
public sealed record NodePageResponse(
    IReadOnlyList<NodeDto> Items,
    string? NextCursor,
    long? Total,
    IReadOnlyList<FacetDto> Facets);
