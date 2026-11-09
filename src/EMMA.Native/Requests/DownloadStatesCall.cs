namespace EMMA.Native.Requests;

/// <summary>Deletes the downloads in some states.</summary>
/// <param name="States">The states to clear; at least one, so that clearing everything is never an accident.</param>
internal sealed record DownloadStatesCall(
    IReadOnlyList<string?>? States);
