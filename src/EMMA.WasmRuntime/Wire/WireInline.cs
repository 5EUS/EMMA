namespace EMMA.WasmRuntime.Wire;

/// <summary>The inline delivery of an asset.</summary>
/// <param name="Base64">The bytes, base64.</param>
internal sealed record WireInline(string Base64);
