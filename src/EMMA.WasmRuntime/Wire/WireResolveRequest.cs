namespace EMMA.WasmRuntime.Wire;

/// <summary>A request to resolve a node.</summary>
/// <param name="Node">The node id.</param>
/// <param name="Role">The role.</param>
/// <param name="Hints">Preferences.</param>
internal sealed record WireResolveRequest(string Node, string Role, IReadOnlyList<WireProp> Hints);
