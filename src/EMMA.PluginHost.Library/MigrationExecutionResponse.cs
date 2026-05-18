namespace EMMA.PluginHost.Library;

/// <summary>
/// Response payload for an executed migration.
/// </summary>
public sealed record MigrationExecutionResponse(
    bool Success,
    string? TargetMediaId,
    int LibrariesAdded,
    int HistoryEntriesMigrated,
    int HistoryEntriesSkipped,
    bool SourceEntriesRemoved,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);