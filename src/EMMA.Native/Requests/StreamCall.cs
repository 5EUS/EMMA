namespace EMMA.Native.Requests;

/// <summary>Opens an asset of a node for playing.</summary>
/// <param name="Ref">The node reference.</param>
/// <param name="Role">The asset role, usually <c>stream</c> or <c>audio</c>.</param>
/// <param name="Index">Which resolved asset; defaults to 0.</param>
/// <param name="Hint">A quality preference passed to the plugin.</param>
internal sealed record StreamCall(
    string? Ref,
    string? Role,
    int? Index,
    string? Hint);
