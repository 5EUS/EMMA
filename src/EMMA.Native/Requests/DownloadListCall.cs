namespace EMMA.Native.Requests;

/// <summary>Lists downloads.</summary>
/// <param name="States">Only downloads in these states (<c>queued</c>, <c>running</c>, <c>paused</c>, <c>completed</c>, <c>failed</c>, <c>canceled</c>); absent for all.</param>
/// <param name="Limit">The most downloads to return; defaults to 200, at most 1000.</param>
internal sealed record DownloadListCall(
    IReadOnlyList<string?>? States,
    int? Limit);
