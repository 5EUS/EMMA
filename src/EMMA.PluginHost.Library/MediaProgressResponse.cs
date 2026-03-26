namespace EMMA.PluginHost.Library;

public sealed record MediaProgressResponse(
    string MediaType,
    string? ChapterId,
    int? PageIndex,
    double? PositionSeconds,
    bool Completed,
    string LastViewedAtUtc);
