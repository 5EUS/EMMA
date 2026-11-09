namespace EMMA.Native.Requests;

/// <summary>Names a download.</summary>
/// <param name="Id">The download id.</param>
internal sealed record DownloadIdCall(
    string? Id);
