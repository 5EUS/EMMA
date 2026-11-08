namespace EMMA.WasmRuntime.Wire;

/// <summary>A count for one filter value.</summary>
/// <param name="Value">The filter value.</param>
/// <param name="Label">Its label.</param>
/// <param name="Count">The number of results.</param>
internal sealed record WireFacetCount(string Value, string Label, ulong Count);
