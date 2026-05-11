namespace EMMA.PluginHost.Library;

/// <summary>
/// Represents an orphaned media item that no longer resolves to a healthy source plugin.
/// </summary>
public sealed record MigrationOrphanedItemResponse(
    string MediaId,
    string OrphanedPluginId,
    string Title,
    string MediaType,
    string? ThumbnailUrl,
    string? Description,
    IReadOnlyDictionary<string, string>? Metadata,
    IReadOnlyList<string> Libraries,
    MediaProgressResponse? Progress,
    HistoryEntryResponse? LatestHistory,
    string OrphanReason);