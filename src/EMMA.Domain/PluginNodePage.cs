namespace EMMA.Domain;

/// <summary>A page of nodes exactly as a plugin returned it. See <see cref="PluginNode"/>.</summary>
/// <param name="Items">The nodes, in natural order.</param>
/// <param name="NextCursor">The cursor for the next page; <see langword="null"/> on the last page.</param>
/// <param name="Total">The known total, when the upstream reports one.</param>
/// <param name="Facets">Result counts per filter value.</param>
public sealed record PluginNodePage(
    IReadOnlyList<PluginNode> Items,
    string? NextCursor,
    long? Total,
    IReadOnlyList<Facet> Facets);
