namespace EMMA.PluginHost.Library;

/// <summary>
/// Response payload for a migration compatibility check.
/// </summary>
public sealed record MigrationCheckResponse(
    bool HasMatch,
    string? SelectedTargetMediaId,
    string? SelectedTargetTitle,
    string? SelectedTargetMediaType,
    string? SelectedTargetThumbnailUrl,
    bool CanMigrateLibrary,
    bool CanMigrateProgress,
    bool CanMigrateHistory,
    IReadOnlyList<string> Libraries,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<MigrationMatchResponse> Matches);