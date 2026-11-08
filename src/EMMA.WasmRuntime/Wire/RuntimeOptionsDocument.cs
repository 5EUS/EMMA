namespace EMMA.WasmRuntime.Wire;

/// <summary>The runtime options document.</summary>
/// <param name="Target">The compile target, such as <c>pulley64</c>; none for the native target.</param>
internal sealed record RuntimeOptionsDocument(string? Target);
