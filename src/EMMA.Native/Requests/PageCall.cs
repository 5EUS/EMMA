namespace EMMA.Native.Requests;

/// <summary>A page of a plugin's browse root (<c>plugin</c>) or of a node's children (<c>ref</c>).</summary>
/// <param name="Plugin">The plugin id, for browsing.</param>
/// <param name="Ref">The parent node reference, for children.</param>
/// <param name="Cursor">The previous page's next cursor.</param>
/// <param name="Limit">A page-size hint.</param>
internal sealed record PageCall(
    string? Plugin,
    string? Ref,
    string? Cursor,
    int? Limit);
