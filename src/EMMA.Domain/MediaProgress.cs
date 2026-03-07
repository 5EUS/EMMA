namespace EMMA.Domain;

public sealed record PagedMediaProgress(
    MediaId MediaId,
    string PluginId,
    string ChapterId,
    int PageIndex,
    bool Completed,
    DateTimeOffset LastViewedAtUtc);

public sealed record VideoMediaProgress(
    MediaId MediaId,
    string PluginId,
    double PositionSeconds,
    bool Completed,
    DateTimeOffset LastViewedAtUtc);
