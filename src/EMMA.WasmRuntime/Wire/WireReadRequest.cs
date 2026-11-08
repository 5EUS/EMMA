namespace EMMA.WasmRuntime.Wire;

/// <summary>A request to read a proxied asset.</summary>
/// <param name="Token">The token.</param>
/// <param name="Range">The range.</param>
internal sealed record WireReadRequest(string Token, WireByteRange? Range);
