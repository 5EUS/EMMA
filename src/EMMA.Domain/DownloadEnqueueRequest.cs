namespace EMMA.Domain;

public sealed record DownloadEnqueueRequest(
    string PluginId,
    string MediaId,
    string MediaType,
    string? ChapterId,
    string? StreamId);
