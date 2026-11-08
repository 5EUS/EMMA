namespace EMMA.Domain;

/// <summary>One page of validated nodes. Order is significant: for children it is reading or playback order.</summary>
/// <param name="Items">The nodes.</param>
/// <param name="NextCursor">The cursor for the next page; <see langword="null"/> on the last page.</param>
/// <param name="Total">The known total, when the upstream reports one.</param>
/// <param name="Facets">Result counts per filter value.</param>
public sealed record NodePage(
    IReadOnlyList<Node> Items,
    string? NextCursor,
    long? Total,
    IReadOnlyList<Facet> Facets);
