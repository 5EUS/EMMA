namespace EMMA.Domain;

/// <summary>A saved node whose children changed since the host last listed them.</summary>
/// <param name="Node">The saved node.</param>
/// <param name="NewChildren">The children the host had not seen before, in the order the plugin lists them.</param>
public sealed record NodeUpdate(Node Node, IReadOnlyList<Node> NewChildren);
