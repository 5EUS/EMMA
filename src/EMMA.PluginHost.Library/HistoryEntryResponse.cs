namespace EMMA.PluginHost.Library;

public sealed record HistoryEntryResponse(
    string EntryId,
    string MediaId,
    string PluginId,
    string ExternalId,
    string UserId,
    double Position,
    bool Completed,
    string LastViewedAtUtc);
