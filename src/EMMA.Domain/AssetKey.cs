using System.Runtime.InteropServices;

namespace EMMA.Domain;

/// <summary>
/// Identifies one cached asset: a node, the role it was resolved for, and a variant
/// that tells alternatives apart (for remote assets, a hash of the URL).
/// </summary>
/// <param name="Node">The node the asset belongs to.</param>
/// <param name="Role">The role it was resolved for.</param>
/// <param name="Variant">Distinguishes alternatives of the same node and role.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct AssetKey(NodeRef Node, AssetRole Role, string Variant)
{
    /// <summary>Formats a stable text form usable as a cache key.</summary>
    /// <returns>The key text.</returns>
    public override string ToString() => $"{this.Node}|{this.Role}|{this.Variant}";
}
