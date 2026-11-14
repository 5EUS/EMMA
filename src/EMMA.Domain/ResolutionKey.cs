using System.Runtime.InteropServices;

namespace EMMA.Domain;

/// <summary>Identifies one resolution: a node, the role it was resolved for, and the hints that shaped the answer.</summary>
/// <param name="Node">The node.</param>
/// <param name="Role">The role.</param>
/// <param name="Hints">A stable text form of the hints; empty for none. A plugin may answer differently per hint, so each set is its own entry.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ResolutionKey(NodeRef Node, AssetRole Role, string Hints)
{
    /// <summary>Builds the key for a resolve request.</summary>
    /// <param name="node">The node.</param>
    /// <param name="role">The role.</param>
    /// <param name="hints">The request's hints.</param>
    /// <returns>The key.</returns>
    public static ResolutionKey For(NodeRef node, AssetRole role, IReadOnlyList<Prop> hints) =>
        new(node, role, string.Join('\n', hints.Select(hint => $"{hint.Key}={hint.Value.Kind}:{hint.Value.Text}:{hint.Value.WholeNumber}:{hint.Value.Number:R}")));
}
