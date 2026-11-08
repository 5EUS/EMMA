namespace EMMA.WasmRuntime.Wire;

/// <summary>The remote delivery of an asset.</summary>
/// <param name="Url">The absolute URL.</param>
/// <param name="Headers">The headers the fetch must carry.</param>
/// <param name="ExpiresMs">When the URL expires.</param>
internal sealed record WireRemote(string Url, IReadOnlyList<WireHeader> Headers, long? ExpiresMs);
