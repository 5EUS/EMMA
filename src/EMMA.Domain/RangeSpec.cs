namespace EMMA.Domain;

/// <summary>Bounds of a <see cref="FilterType.NumberRange"/> filter.</summary>
/// <param name="Min">The minimum, if bounded.</param>
/// <param name="Max">The maximum, if bounded.</param>
/// <param name="Step">The step, if any.</param>
/// <param name="Unit">The unit label, if any.</param>
public sealed record RangeSpec(double? Min, double? Max, double? Step, string? Unit);
