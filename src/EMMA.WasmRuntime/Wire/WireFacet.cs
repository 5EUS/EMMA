namespace EMMA.WasmRuntime.Wire;

/// <summary>Counts for one filter.</summary>
/// <param name="FilterId">The filter id.</param>
/// <param name="Counts">The counts.</param>
internal sealed record WireFacet(string FilterId, IReadOnlyList<WireFacetCount> Counts);
