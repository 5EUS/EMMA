namespace EMMA.Native.Requests;

/// <summary>Records a position.</summary>
/// <param name="Ref">The node reference.</param>
/// <param name="Unit">The unit, such as <c>page</c> or <c>ms</c>.</param>
/// <param name="Value">The position.</param>
/// <param name="Total">The extent.</param>
/// <param name="Completed">Whether the node is finished.</param>
internal sealed record ProgressCall(
    string? Ref,
    string? Unit,
    double? Value,
    double? Total,
    bool? Completed);
