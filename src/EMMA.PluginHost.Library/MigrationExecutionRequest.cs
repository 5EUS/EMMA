namespace EMMA.PluginHost.Library;

/// <summary>
/// Request payload for executing a migration from an orphaned media item to a target plugin.
/// </summary>
public sealed record MigrationExecutionRequest(
    string MediaId,
    string OrphanedPluginId,
    string Title,
    string MediaType,
    string TargetPluginId,
    string? TargetMediaId,
    string? QueryOverride,
    bool RemoveOrphanedEntriesAfterSuccess);