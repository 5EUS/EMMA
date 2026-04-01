using System.Collections.Generic;

namespace EMMA.Plugin.Common;

public sealed record HandshakeResponse(string version, string message);

public sealed record SearchItem(
    string id,
    string source,
    string title,
    string mediaType,
    string? thumbnailUrl = null,
    string? description = null);

public sealed record CapabilityItem(string name, string[] mediaTypes, string[] operations);

public sealed record ChapterItem(string id, int number, string title, string[]? uploaderGroups = null);

public sealed record PageItem(string id, int index, string contentUri);

public sealed record OperationRequest(
    string operation,
    string? mediaId,
    string? mediaType,
    string? argsJson,
    string? payloadJson);

public sealed record OperationResult(
    bool isError,
    string? error,
    string contentType,
    string payloadJson)
{
    public static OperationResult Error(string error)
        => new(true, error, "application/problem+json", "");

    public static OperationResult UnsupportedOperation(string operation)
        => Error($"unsupported-operation:{operation?.Trim() ?? string.Empty}");

    public static OperationResult InvalidArguments(string message)
        => Error($"invalid-arguments:{message?.Trim() ?? string.Empty}");

    public static OperationResult Failed(string message)
        => Error($"failed:{message?.Trim() ?? string.Empty}");
}

public sealed record BenchmarkResult(int iterations, long checksum, int generatedBytes, long elapsedMs);

public sealed record NetworkBenchmarkResult(string query, int payloadBytes, int itemCount, long elapsedMs);

public sealed record ChapterOperationItem(string id, int number, string title, string[] uploaderGroups);

public sealed record VideoTrackOperationItem(
    string id,
    string label,
    string? language = null,
    string? codec = null,
    bool isDefault = false);

public sealed record VideoStreamOperationItem(
    string id,
    string label,
    string playlistUri,
    IReadOnlyDictionary<string, string>? requestHeaders = null,
    string? requestCookies = null,
    string? streamType = null,
    bool isLive = false,
    bool drmProtected = false,
    string? drmScheme = null,
    IReadOnlyList<VideoTrackOperationItem>? audioTracks = null,
    IReadOnlyList<VideoTrackOperationItem>? subtitleTracks = null,
    string? defaultAudioTrackId = null,
    string? defaultSubtitleTrackId = null);

public sealed record VideoSegmentOperationItem(string contentType, string payload);