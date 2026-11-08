namespace EMMA.Domain;

/// <summary>A request to resolve a node to its assets for a role.</summary>
/// <param name="Node">The plugin-local node id.</param>
/// <param name="Role">One of the roles declared for the node's kind.</param>
/// <param name="Hints">Optional preferences such as <c>emma.quality</c>; a plugin may ignore any.</param>
public sealed record ResolveRequest(LocalNodeId Node, AssetRole Role, IReadOnlyList<Prop> Hints);
