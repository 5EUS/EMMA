namespace EMMA.Native.Requests;

/// <summary>Reads an asset.</summary>
/// <param name="Ref">The node reference.</param>
/// <param name="Role">The asset role.</param>
/// <param name="Index">Which resolved asset; defaults to 0.</param>
/// <param name="Range">A byte range, honoured for assets the plugin serves itself and for stored downloads.</param>
/// <param name="Hint">A preference passed to the plugin, such as a quality (<c>1080p</c>).</param>
internal sealed record AssetCall(
    string? Ref,
    string? Role,
    int? Index,
    RangeCall? Range,
    string? Hint);
