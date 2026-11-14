namespace EMMA.Api.Contracts;

/// <summary>Everything a player needs to play a node.</summary>
/// <param name="Ref">The node.</param>
/// <param name="Primary">The role the node's own media is in: <c>stream</c> or <c>audio</c>.</param>
/// <param name="Sources">The alternatives for the primary role: qualities of a stream, or files of a track.</param>
/// <param name="Subtitles">Subtitle files.</param>
/// <param name="AudioTracks">Alternate audio tracks of a stream.</param>
/// <param name="Chapters">Chapter markers.</param>
/// <param name="DurationMs">The length in milliseconds, when a source says.</param>
public sealed record PlaybackPlanResponse(
    string Ref,
    string Primary,
    IReadOnlyList<PlaybackTrackDto> Sources,
    IReadOnlyList<PlaybackTrackDto> Subtitles,
    IReadOnlyList<PlaybackTrackDto> AudioTracks,
    IReadOnlyList<PlaybackChapterDto> Chapters,
    long? DurationMs);
