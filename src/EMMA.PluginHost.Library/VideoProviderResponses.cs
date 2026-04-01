namespace EMMA.PluginHost.Library;

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

public sealed record VideoTrackResponse(
    string Id,
    string Label,
    string? Language = null,
    string? Codec = null,
    bool IsDefault = false);

public sealed record VideoStreamsResponse(
    IReadOnlyList<VideoStreamResponse> Streams);

public sealed record VideoSegmentAssetResponse(
    string ContentType,
    byte[] Payload,
    string FetchedAtUtc);
