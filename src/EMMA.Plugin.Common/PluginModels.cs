using System.Collections.Generic;

namespace EMMA.Plugin.Common;

/// <summary>
/// Represents the plugin handshake payload returned to the host.
/// </summary>
/// <param name="version">The plugin version string.</param>
/// <param name="message">The descriptive handshake message.</param>
public sealed record HandshakeResponse(string version, string message);

/// <summary>
/// Represents a simple metadata key-value pair.
/// </summary>
/// <param name="key">The metadata key.</param>
/// <param name="value">The metadata value.</param>
public sealed record MetadataItem(string key, string value);

/// <summary>
/// Represents a media search result returned by a plugin.
/// </summary>
/// <param name="id">The source-specific media identifier.</param>
/// <param name="source">The source name that produced the result.</param>
/// <param name="title">The display title of the media item.</param>
/// <param name="mediaType">The EMMA media type for the item.</param>
/// <param name="thumbnailUrl">An optional thumbnail URL.</param>
/// <param name="description">An optional summary or description.</param>
/// <param name="metadata">Optional additional metadata entries.</param>
public sealed record SearchItem(
    string id,
    string source,
    string title,
    string mediaType,
    string? thumbnailUrl = null,
    string? description = null,
    IReadOnlyList<MetadataItem>? metadata = null);

/// <summary>
/// Enriches search results with provider-specific metadata.
/// </summary>
public interface IPluginSearchMetadataEnricher
{
    /// <summary>
    /// Enriches search items with additional metadata.
    /// </summary>
    /// <param name="items">The search items to enrich.</param>
    /// <param name="cancellationToken">The cancellation token for the enrichment flow.</param>
    /// <returns>The enriched search items.</returns>
    Task<IReadOnlyList<SearchItem>> EnrichSearchItemsAsync(
        IReadOnlyList<SearchItem> items,
        CancellationToken cancellationToken);
}

    /// <summary>
    /// Represents a capability bucket advertised by a plugin.
    /// </summary>
    /// <param name="name">The capability group name.</param>
    /// <param name="mediaTypes">The media types covered by the capability.</param>
    /// <param name="operations">The operations supported by the capability.</param>
public sealed record CapabilityItem(string name, string[] mediaTypes, string[] operations);

    /// <summary>
    /// Represents a chapter entry exposed by a paged-media plugin.
    /// </summary>
    /// <param name="id">The chapter identifier.</param>
    /// <param name="number">The numeric chapter order.</param>
    /// <param name="title">The display title for the chapter.</param>
    /// <param name="uploaderGroups">Optional uploader or scanlation group names.</param>
public sealed record ChapterItem(string id, int number, string title, string[]? uploaderGroups = null);

    /// <summary>
    /// Represents a single page returned by a paged-media plugin.
    /// </summary>
    /// <param name="id">The page identifier.</param>
    /// <param name="index">The zero-based page index.</param>
    /// <param name="contentUri">The content URI for the page asset.</param>
public sealed record PageItem(string id, int index, string contentUri);

    /// <summary>
    /// Represents a generic plugin operation request.
    /// </summary>
    /// <param name="operation">The operation name to execute.</param>
    /// <param name="mediaId">The optional media identifier.</param>
    /// <param name="mediaType">The optional media type.</param>
    /// <param name="argsJson">The optional JSON argument payload.</param>
    /// <param name="payloadJson">The optional JSON body payload.</param>
public sealed record OperationRequest(
    string operation,
    string? mediaId,
    string? mediaType,
    string? argsJson,
    string? payloadJson);

/// <summary>
/// Represents the typed result of a plugin operation.
/// </summary>
/// <param name="isError">Indicates whether the result is an error.</param>
/// <param name="error">The serialized error payload when one exists.</param>
/// <param name="contentType">The payload content type.</param>
/// <param name="payloadJson">The serialized JSON payload.</param>
public sealed record OperationResult(
    bool isError,
    string? error,
    string contentType,
    string payloadJson)
{
    /// <summary>
    /// Creates an error result with a problem-json content type.
    /// </summary>
    /// <param name="error">The serialized error text to include in the result.</param>
    /// <returns>An error operation result.</returns>
    public static OperationResult Error(string error)
        => new(true, error, "application/problem+json", "");

    /// <summary>
    /// Creates an unsupported-operation error result.
    /// </summary>
    /// <param name="operation">The unsupported operation name.</param>
    /// <returns>An error result describing the unsupported operation.</returns>
    public static OperationResult UnsupportedOperation(string operation)
        => Error($"unsupported-operation:{operation?.Trim() ?? string.Empty}");

    /// <summary>
    /// Creates an invalid-arguments error result.
    /// </summary>
    /// <param name="message">The validation message to include.</param>
    /// <returns>An error result describing the invalid arguments.</returns>
    public static OperationResult InvalidArguments(string message)
        => Error($"invalid-arguments:{message?.Trim() ?? string.Empty}");

    /// <summary>
    /// Creates a generic failed-operation error result.
    /// </summary>
    /// <param name="message">The failure message to include.</param>
    /// <returns>An error result describing the failure.</returns>
    public static OperationResult Failed(string message)
        => Error($"failed:{message?.Trim() ?? string.Empty}");
}

/// <summary>
/// Represents the result of a local benchmark operation.
/// </summary>
/// <param name="iterations">The number of iterations performed.</param>
/// <param name="checksum">The checksum produced by the benchmark.</param>
/// <param name="generatedBytes">The number of bytes generated during the benchmark.</param>
/// <param name="elapsedMs">The elapsed time in milliseconds.</param>
public sealed record BenchmarkResult(int iterations, long checksum, int generatedBytes, long elapsedMs);

/// <summary>
/// Represents the result of a network benchmark operation.
/// </summary>
/// <param name="query">The benchmark query used for the request.</param>
/// <param name="payloadBytes">The size of the fetched payload in bytes.</param>
/// <param name="itemCount">The number of items parsed from the payload.</param>
/// <param name="elapsedMs">The elapsed time in milliseconds.</param>
public sealed record NetworkBenchmarkResult(string query, int payloadBytes, int itemCount, long elapsedMs);

/// <summary>
/// Represents a chapter item returned from an invoke-style operation.
/// </summary>
/// <param name="id">The chapter identifier.</param>
/// <param name="number">The chapter number.</param>
/// <param name="title">The chapter title.</param>
/// <param name="uploaderGroups">The uploader group names associated with the chapter.</param>
public sealed record ChapterOperationItem(string id, int number, string title, string[] uploaderGroups);

/// <summary>
/// Represents an audio or subtitle track attached to a video stream.
/// </summary>
/// <param name="id">The track identifier.</param>
/// <param name="label">The display label for the track.</param>
/// <param name="language">The optional track language.</param>
/// <param name="codec">The optional track codec.</param>
/// <param name="isDefault">Indicates whether the track is the default selection.</param>
public sealed record VideoTrackOperationItem(
    string id,
    string label,
    string? language = null,
    string? codec = null,
    bool isDefault = false);

/// <summary>
/// Represents a playable video stream and its associated track metadata.
/// </summary>
/// <param name="id">The stream identifier.</param>
/// <param name="label">The display label for the stream.</param>
/// <param name="playlistUri">The playlist URI for the stream.</param>
/// <param name="requestHeaders">Optional request headers required to fetch stream data.</param>
/// <param name="requestCookies">Optional request cookies required to fetch stream data.</param>
/// <param name="streamType">The optional stream type label.</param>
/// <param name="isLive">Indicates whether the stream is live.</param>
/// <param name="drmProtected">Indicates whether the stream is DRM protected.</param>
/// <param name="drmScheme">The optional DRM scheme identifier.</param>
/// <param name="audioTracks">The optional audio tracks exposed by the stream.</param>
/// <param name="subtitleTracks">The optional subtitle tracks exposed by the stream.</param>
/// <param name="defaultAudioTrackId">The optional default audio track identifier.</param>
/// <param name="defaultSubtitleTrackId">The optional default subtitle track identifier.</param>
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

/// <summary>
/// Represents a video segment payload returned by an invoke operation.
/// </summary>
/// <param name="contentType">The segment content type.</param>
/// <param name="payload">The serialized segment payload.</param>
public sealed record VideoSegmentOperationItem(string contentType, string payload);