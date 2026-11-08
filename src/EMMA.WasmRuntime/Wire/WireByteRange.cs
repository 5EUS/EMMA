namespace EMMA.WasmRuntime.Wire;

/// <summary>A byte range.</summary>
/// <param name="Start">The first byte.</param>
/// <param name="Length">The length, or to the end.</param>
internal sealed record WireByteRange(ulong Start, ulong? Length);
