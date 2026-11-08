namespace EMMA.WasmRuntime.Wire;

/// <summary>A node as a plugin returned it.</summary>
/// <param name="Id">The plugin-local id.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Title">The title.</param>
/// <param name="Parent">The parent id.</param>
/// <param name="Thumbnail">A thumbnail URL.</param>
/// <param name="Props">Descriptive data.</param>
internal sealed record WireNode(string Id, string Kind, string Title, string? Parent, string? Thumbnail, IReadOnlyList<WireProp> Props);
