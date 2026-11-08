namespace EMMA.WasmRuntime.Wire;

/// <summary>How a lookup filter searches.</summary>
/// <param name="MinQueryLength">The shortest query.</param>
/// <param name="MaxResults">The most results.</param>
/// <param name="DebounceMs">The debounce interval.</param>
/// <param name="AllowCustomValues">Whether free text is allowed.</param>
internal sealed record WireLookup(uint MinQueryLength, uint MaxResults, uint DebounceMs, bool AllowCustomValues);
