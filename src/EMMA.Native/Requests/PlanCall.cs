namespace EMMA.Native.Requests;

/// <summary>Asks for the playback plan of a node.</summary>
/// <param name="Ref">The node reference.</param>
/// <param name="Hint">A quality preference passed to the plugin.</param>
internal sealed record PlanCall(
    string? Ref,
    string? Hint);
