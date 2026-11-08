namespace EMMA.WasmRuntime.Wire;

/// <summary>A page of nodes.</summary>
/// <param name="Items">The nodes.</param>
/// <param name="NextCursor">The next cursor.</param>
/// <param name="Total">The known total.</param>
/// <param name="Facets">Counts per filter value.</param>
internal sealed record WireNodePage(IReadOnlyList<WireNode> Items, string? NextCursor, ulong? Total, IReadOnlyList<WireFacet> Facets);
