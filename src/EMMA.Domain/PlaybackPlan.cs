namespace EMMA.Domain;

/// <summary>
/// Everything a player needs to play a node, gathered from what the plugin resolves for the roles its kind declares.
/// The plan never says where bytes live: a client asks the host to open one of the tracks and plays what it is given.
/// </summary>
/// <param name="Node">The node.</param>
/// <param name="Primary">The role the node's own media is in: <c>stream</c> when the kind declares it, otherwise <c>audio</c>.</param>
/// <param name="Sources">The alternatives for the primary role: qualities of a stream, or files of a track.</param>
/// <param name="Subtitles">Subtitle files, when the kind declares them.</param>
/// <param name="AudioTracks">Alternate audio tracks of a stream, when the kind declares them.</param>
/// <param name="Chapters">Chapter markers, in the order the plugin gave them.</param>
/// <param name="DurationMs">The length in milliseconds, when a source says.</param>
public sealed record PlaybackPlan(
    NodeRef Node,
    AssetRole Primary,
    IReadOnlyList<PlaybackTrack> Sources,
    IReadOnlyList<PlaybackTrack> Subtitles,
    IReadOnlyList<PlaybackTrack> AudioTracks,
    IReadOnlyList<PlaybackChapter> Chapters,
    long? DurationMs);
