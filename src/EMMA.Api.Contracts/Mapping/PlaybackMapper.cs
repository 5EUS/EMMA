using EMMA.Api.Contracts;
using EMMA.Domain;

namespace EMMA.Api.Mapping;

/// <summary>Maps what a player needs to its wire form. Where the bytes live never appears.</summary>
public static class PlaybackMapper
{
    /// <summary>Maps a plan.</summary>
    /// <param name="plan">The plan.</param>
    /// <returns>The wire form.</returns>
    public static PlaybackPlanResponse ToDto(PlaybackPlan plan) => new(
        plan.Node.ToString(),
        plan.Primary.Value,
        [.. plan.Sources.Select(ToDto)],
        [.. plan.Subtitles.Select(ToDto)],
        [.. plan.AudioTracks.Select(ToDto)],
        [.. plan.Chapters.Select(chapter => new PlaybackChapterDto(chapter.Index, chapter.StartMs, chapter.EndMs, chapter.Title))],
        plan.DurationMs);

    private static PlaybackTrackDto ToDto(PlaybackTrack track) => new(
        track.Role.Value,
        track.Index,
        track.Mime,
        track.Size,
        track.Label,
        track.Language,
        track.Quality,
        track.Width,
        track.Height,
        track.Bitrate,
        track.IsDefault,
        track.IsLive,
        track.DrmScheme,
        track.DrmLicenseUrl,
        track.DurationMs);
}
