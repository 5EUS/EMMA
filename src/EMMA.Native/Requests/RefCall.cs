namespace EMMA.Native.Requests;

/// <summary>Names a node.</summary>
/// <param name="Ref">The node reference, <c>plugin.id:local-id</c>.</param>
internal sealed record RefCall(
    string? Ref);
