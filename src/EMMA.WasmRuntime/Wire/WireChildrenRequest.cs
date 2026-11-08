namespace EMMA.WasmRuntime.Wire;

/// <summary>A request for children.</summary>
/// <param name="Parent">The parent id, or none for the root.</param>
/// <param name="Request">The page request.</param>
internal sealed record WireChildrenRequest(string? Parent, WirePageRequest Request);
