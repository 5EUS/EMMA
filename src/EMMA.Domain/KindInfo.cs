namespace EMMA.Domain;

/// <summary>Declares one kind of node a plugin produces: the schema generic clients navigate by.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Label">A human label for the kind.</param>
/// <param name="Children">Kinds allowed beneath this one; empty means a leaf.</param>
/// <param name="Roles">Asset roles the kind can resolve.</param>
/// <param name="Profile">An optional client profile name such as <c>emma.profile.paged@1</c>.</param>
public sealed record KindInfo(
    NodeKind Kind,
    string Label,
    IReadOnlyList<NodeKind> Children,
    IReadOnlyList<AssetRole> Roles,
    string? Profile);
