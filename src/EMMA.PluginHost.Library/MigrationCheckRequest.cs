namespace EMMA.PluginHost.Library;

/// <summary>
/// Request payload for validating whether an orphaned media item can migrate to a target plugin.
/// </summary>
public sealed record MigrationCheckRequest(
    string MediaId,
    string OrphanedPluginId,
    string Title,
    string MediaType,
    string TargetPluginId,
    string? QueryOverride,
    string? TargetMediaId);