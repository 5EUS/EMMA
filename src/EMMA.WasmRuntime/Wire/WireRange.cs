namespace EMMA.WasmRuntime.Wire;

/// <summary>The bounds of a number-range filter.</summary>
/// <param name="Min">The minimum.</param>
/// <param name="Max">The maximum.</param>
/// <param name="Step">The step.</param>
/// <param name="Unit">The unit.</param>
internal sealed record WireRange(double? Min, double? Max, double? Step, string? Unit);
