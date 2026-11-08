namespace EMMA.Native.Requests;

/// <summary>Lists recent progress.</summary>
/// <param name="Limit">The most entries to return.</param>
internal sealed record RecentCall(
    int? Limit);
