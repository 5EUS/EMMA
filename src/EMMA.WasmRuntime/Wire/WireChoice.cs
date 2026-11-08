namespace EMMA.WasmRuntime.Wire;

/// <summary>An option of a filter, sort or setting.</summary>
/// <param name="Value">The value.</param>
/// <param name="Label">The label.</param>
/// <param name="Description">A description.</param>
/// <param name="IsDefault">Whether it is the default.</param>
internal sealed record WireChoice(string Value, string Label, string? Description, bool IsDefault);
