namespace EMMA.WasmRuntime.Wire;

/// <summary>The JSON part of a <c>read</c> result; the bytes travel separately.</summary>
/// <param name="Total">The total size of the asset, when known.</param>
internal sealed record WireChunkInfo(ulong? Total);
