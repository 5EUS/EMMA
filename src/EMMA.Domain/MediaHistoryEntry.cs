namespace EMMA.Domain;

/// <summary>
/// Read/watch history entry.
/// </summary>
public sealed record MediaHistoryEntry(
    string EntryId,
    MediaId MediaId,
    string PluginId,
    string ExternalId,
    string UserId,
    double Position,
    bool Completed,
    DateTimeOffset LastViewedAtUtc);
