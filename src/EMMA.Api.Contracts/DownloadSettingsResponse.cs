namespace EMMA.Api.Contracts;

/// <summary>How the download executor is set up. Downloads belong to the host, so these apply to every client.</summary>
/// <param name="Parallel">How many downloads run at once.</param>
public sealed record DownloadSettingsResponse(
    int Parallel);
