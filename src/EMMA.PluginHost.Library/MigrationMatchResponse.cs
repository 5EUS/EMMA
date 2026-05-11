namespace EMMA.PluginHost.Library;

/// <summary>
/// Represents one candidate target media match for a migration check.
/// </summary>
public sealed record MigrationMatchResponse(
    string MediaId,
    string Title,
    string MediaType,
    string? ThumbnailUrl,
    int Score,
    bool ExactTitleMatch);