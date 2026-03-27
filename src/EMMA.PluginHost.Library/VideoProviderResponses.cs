namespace EMMA.PluginHost.Library;

public sealed record VideoStreamResponse(
    string Id,
    string Label,
    string PlaylistUri);

public sealed record VideoStreamsResponse(
    IReadOnlyList<VideoStreamResponse> Streams);

public sealed record VideoSegmentAssetResponse(
    string ContentType,
    byte[] Payload,
    string FetchedAtUtc);
