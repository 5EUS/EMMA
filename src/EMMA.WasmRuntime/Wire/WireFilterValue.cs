namespace EMMA.WasmRuntime.Wire;

/// <summary>A chosen filter value.</summary>
/// <param name="Id">The filter id.</param>
/// <param name="Values">The values.</param>
internal sealed record WireFilterValue(string Id, IReadOnlyList<string> Values);
