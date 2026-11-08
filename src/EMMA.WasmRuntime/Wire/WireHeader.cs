namespace EMMA.WasmRuntime.Wire;

/// <summary>A request header.</summary>
/// <param name="Name">The name.</param>
/// <param name="Value">The value.</param>
internal sealed record WireHeader(string Name, string Value);
