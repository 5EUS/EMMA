namespace EMMA.PluginHost.Library;

/// <summary>
/// Represents a playable video stream returned by the host library.
/// </summary>
/// <param name="Id">The stream identifier.</param>
/// <param name="Label">The display label for the stream.</param>
/// <param name="PlaylistUri">The playlist URI for the stream.</param>
/// <param name="RequestHeaders">Optional request headers needed to access the stream.</param>
/// <param name="RequestCookies">Optional request cookies needed to access the stream.</param>
/// <param name="StreamType">The optional stream type label.</param>
/// <param name="IsLive">Indicates whether the stream is live.</param>
/// <param name="DrmProtected">Indicates whether the stream is DRM protected.</param>
/// <param name="DrmScheme">The optional DRM scheme identifier.</param>
/// <param name="AudioTracks">The optional audio tracks.</param>
/// <param name="SubtitleTracks">The optional subtitle tracks.</param>
/// <param name="DefaultAudioTrackId">The optional default audio track identifier.</param>
/// <param name="DefaultSubtitleTrackId">The optional default subtitle track identifier.</param>
public sealed record VideoStreamResponse(
    string Id,
    string Label,
    string PlaylistUri,
    IReadOnlyDictionary<string, string>? RequestHeaders = null,
    string? RequestCookies = null,
    string? StreamType = null,
    bool IsLive = false,
    bool DrmProtected = false,
    string? DrmScheme = null,
    IReadOnlyList<VideoTrackResponse>? AudioTracks = null,
    IReadOnlyList<VideoTrackResponse>? SubtitleTracks = null,
    string? DefaultAudioTrackId = null,
    string? DefaultSubtitleTrackId = null);

/// <summary>
/// Represents an audio or subtitle track exposed by a video stream.
/// </summary>
/// <param name="Id">The track identifier.</param>
/// <param name="Label">The display label.</param>
/// <param name="Language">The optional language code or label.</param>
/// <param name="Codec">The optional codec identifier.</param>
/// <param name="IsDefault">Indicates whether this track is the default selection.</param>
public sealed record VideoTrackResponse(
    string Id,
    string Label,
    string? Language = null,
    string? Codec = null,
    bool IsDefault = false);

/// <summary>
/// Wraps the set of streams available for a media item.
/// </summary>
/// <param name="Streams">The available streams.</param>
public sealed record VideoStreamsResponse(
    IReadOnlyList<VideoStreamResponse> Streams);

/// <summary>
/// Represents a fetched video segment asset.
/// </summary>
/// <param name="ContentType">The segment content type.</param>
/// <param name="Payload">The segment payload bytes.</param>
/// <param name="FetchedAtUtc">The fetch timestamp in UTC.</param>
public sealed record VideoSegmentAssetResponse(
    string ContentType,
    byte[] Payload,
    string FetchedAtUtc);
