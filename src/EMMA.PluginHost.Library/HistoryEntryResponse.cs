namespace EMMA.PluginHost.Library;

/// <summary>
/// Represents a persisted media history entry.
/// </summary>
/// <param name="EntryId">The history entry identifier.</param>
/// <param name="MediaId">The media identifier.</param>
/// <param name="PluginId">The plugin identifier.</param>
/// <param name="ExternalId">The provider-specific external identifier.</param>
/// <param name="UserId">The user identifier associated with the history entry.</param>
/// <param name="Position">The current playback or reading position.</param>
/// <param name="Completed">Indicates whether the item was completed.</param>
/// <param name="LastViewedAtUtc">The last-viewed timestamp in UTC.</param>
public sealed record HistoryEntryResponse(
    string EntryId,
    string MediaId,
    string PluginId,
    string ExternalId,
    string UserId,
    double Position,
    bool Completed,
    string LastViewedAtUtc);
