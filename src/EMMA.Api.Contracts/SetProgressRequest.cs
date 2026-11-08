namespace EMMA.Api.Contracts;

/// <summary>Where the caller is within a node.</summary>
/// <param name="Unit">The unit, such as <c>page</c> or <c>ms</c>.</param>
/// <param name="Value">The position.</param>
/// <param name="Total">The extent in the same unit.</param>
/// <param name="Completed">Whether the node is finished.</param>
public sealed record SetProgressRequest(
    string? Unit,
    double? Value,
    double? Total,
    bool? Completed);
