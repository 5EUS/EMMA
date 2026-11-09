namespace EMMA.Api.Contracts;

/// <summary>How many downloads a bulk call changed.</summary>
/// <param name="Count">The number of downloads; zero is not an error.</param>
public sealed record DownloadCountResponse(
    int Count);
