namespace EMMA.WasmRuntime.Wire;

/// <summary>A key and a typed value.</summary>
/// <param name="Key">The key.</param>
/// <param name="Value">The value.</param>
internal sealed record WireProp(string Key, WirePropValue Value);
