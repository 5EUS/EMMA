namespace EMMA.Native.Requests;

/// <summary>Reads an asset.</summary>
/// <param name="Ref">The node reference.</param>
/// <param name="Role">The asset role.</param>
/// <param name="Index">Which resolved asset; defaults to 0.</param>
/// <param name="Range">A byte range, honoured for assets the plugin serves itself.</param>
internal sealed record AssetCall(
    string? Ref,
    string? Role,
    int? Index,
    RangeCall? Range);
