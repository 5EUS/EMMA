namespace EMMA.Domain;

/// <summary>
/// Where a user is within a node, in an open unit: <c>page</c>, <c>ms</c>,
/// <c>paragraph</c>. The host never interprets the unit.
/// </summary>
/// <param name="Unit">The unit token, lower-case.</param>
/// <param name="Value">The position, zero or more.</param>
/// <param name="Total">The extent in the same unit, when known.</param>
public sealed record ProgressLocator(string Unit, double Value, double? Total);
