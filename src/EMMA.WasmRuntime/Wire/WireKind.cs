namespace EMMA.WasmRuntime.Wire;

/// <summary>A declared kind of node.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Label">The label.</param>
/// <param name="Children">The child kinds.</param>
/// <param name="Roles">The asset roles.</param>
/// <param name="Profile">The client profile.</param>
internal sealed record WireKind(string Kind, string Label, IReadOnlyList<string> Children, IReadOnlyList<string> Roles, string? Profile);
