namespace EMMA.PluginHost.Library;

/// <summary>
/// Represents reading or playback progress for a media item.
/// </summary>
/// <param name="MediaType">The media type.</param>
/// <param name="ChapterId">The optional chapter identifier.</param>
/// <param name="PageIndex">The optional page index.</param>
/// <param name="PositionSeconds">The optional playback position in seconds.</param>
/// <param name="Completed">Indicates whether progress is complete.</param>
/// <param name="LastViewedAtUtc">The last-viewed timestamp in UTC.</param>
public sealed record MediaProgressResponse(
    string MediaType,
    string? ChapterId,
    int? PageIndex,
    double? PositionSeconds,
    bool Completed,
    string LastViewedAtUtc);
