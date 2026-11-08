namespace EMMA.Api.Contracts;

/// <summary>Bounds of a number-range filter.</summary>
/// <param name="Min">The minimum.</param>
/// <param name="Max">The maximum.</param>
/// <param name="Step">The step.</param>
/// <param name="Unit">The unit label.</param>
public sealed record RangeDto(
    double? Min,
    double? Max,
    double? Step,
    string? Unit);
