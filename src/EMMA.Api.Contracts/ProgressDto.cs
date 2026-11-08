namespace EMMA.Api.Contracts;

/// <summary>A user's progress in a node.</summary>
/// <param name="Node">The node reference.</param>
/// <param name="Unit">The unit.</param>
/// <param name="Value">The position.</param>
/// <param name="Total">The extent.</param>
/// <param name="Completed">Whether the node is finished.</param>
/// <param name="UpdatedAt">When it was recorded.</param>
public sealed record ProgressDto(
    string Node,
    string Unit,
    double Value,
    double? Total,
    bool Completed,
    DateTimeOffset UpdatedAt);
