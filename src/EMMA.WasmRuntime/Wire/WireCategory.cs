namespace EMMA.WasmRuntime.Wire;

/// <summary>A settings group.</summary>
/// <param name="Id">The id.</param>
/// <param name="Label">The label.</param>
/// <param name="Description">A description.</param>
internal sealed record WireCategory(string Id, string Label, string? Description);
