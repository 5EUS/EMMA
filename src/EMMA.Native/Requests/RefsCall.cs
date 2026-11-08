namespace EMMA.Native.Requests;

/// <summary>Names several nodes.</summary>
/// <param name="Refs">The node references.</param>
internal sealed record RefsCall(
    List<string?>? Refs);
