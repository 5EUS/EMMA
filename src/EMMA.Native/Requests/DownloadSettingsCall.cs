namespace EMMA.Native.Requests;

/// <summary>Changes how the download executor is set up.</summary>
/// <param name="Parallel">How many downloads run at once, 1 to 8.</param>
internal sealed record DownloadSettingsCall(
    int? Parallel);
