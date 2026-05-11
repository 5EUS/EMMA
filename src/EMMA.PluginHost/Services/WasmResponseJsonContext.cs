using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using EMMA.Plugin.Common;

namespace EMMA.PluginHost.Services;

/// <summary>
/// Source-generated JSON serialization context for WASM response types.
/// Required for NativeAOT compatibility where reflection-based serialization is disabled.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(MetadataItem))]
[JsonSerializable(typeof(IReadOnlyList<MetadataItem>))]
[JsonSerializable(typeof(List<MetadataItem>))]
[JsonSerializable(typeof(WasmSearchItem))]
[JsonSerializable(typeof(IReadOnlyList<WasmSearchItem>))]
[JsonSerializable(typeof(List<WasmSearchItem>))]
[JsonSerializable(typeof(WasmHealth))]
[JsonSerializable(typeof(WasmChapterItem))]
[JsonSerializable(typeof(IReadOnlyList<WasmChapterItem>))]
[JsonSerializable(typeof(List<WasmChapterItem>))]
[JsonSerializable(typeof(WasmPageItem))]
[JsonSerializable(typeof(IReadOnlyList<WasmPageItem>))]
[JsonSerializable(typeof(List<WasmPageItem>))]
[JsonSerializable(typeof(WasmOperationResult))]
[JsonSerializable(typeof(WasmVideoStreamItem))]
[JsonSerializable(typeof(IReadOnlyList<WasmVideoStreamItem>))]
[JsonSerializable(typeof(List<WasmVideoStreamItem>))]
[JsonSerializable(typeof(WasmVideoTrackItem))]
[JsonSerializable(typeof(IReadOnlyList<WasmVideoTrackItem>))]
[JsonSerializable(typeof(List<WasmVideoTrackItem>))]
[JsonSerializable(typeof(WasmVideoSegmentWire))]
[JsonSerializable(typeof(WasmVideoSegmentArgs))]
[JsonSerializable(typeof(WasmCapabilityItem))]
[JsonSerializable(typeof(IReadOnlyList<WasmCapabilityItem>))]
[JsonSerializable(typeof(List<WasmCapabilityItem>))]
[JsonSerializable(typeof(WasmQueryArgs))]
[JsonSerializable(typeof(WasmSearchSuggestionsArgs))]
[JsonSerializable(typeof(WasmSearchSuggestionItem))]
[JsonSerializable(typeof(IReadOnlyList<WasmSearchSuggestionItem>))]
[JsonSerializable(typeof(List<WasmSearchSuggestionItem>))]
[JsonSerializable(typeof(WasmEnrichMediaArgs))]
[JsonSerializable(typeof(WasmSearchFilterArg))]
[JsonSerializable(typeof(WasmSearchQueryAdditionArg))]
[JsonSerializable(typeof(IReadOnlyList<WasmSearchFilterArg>))]
[JsonSerializable(typeof(List<WasmSearchFilterArg>))]
[JsonSerializable(typeof(IReadOnlyList<WasmSearchQueryAdditionArg>))]
[JsonSerializable(typeof(List<WasmSearchQueryAdditionArg>))]
[JsonSerializable(typeof(WasmBenchmarkArgs))]
[JsonSerializable(typeof(WasmPageArgs))]
[JsonSerializable(typeof(WasmPagesArgs))]
public partial class WasmResponseJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Search result item returned from WASM component.
/// </summary>
public sealed record WasmSearchItem(
    string Id,
    string? Source,
    string Title,
    string? MediaType,
    string? ThumbnailUrl = null,
    string? Description = null,
    [property: JsonPropertyName("metadata")] IReadOnlyList<MetadataItem>? Metadata = null);

/// <summary>
/// Health/handshake response from WASM component.
/// </summary>
public sealed record WasmHealth(string? Version, string? Message);

/// <summary>
/// Chapter item returned from WASM component.
/// </summary>
public sealed record WasmChapterItem(string Id, int Number, string Title, IReadOnlyList<string>? UploaderGroups = null);

/// <summary>
/// Page item returned from WASM component.
/// </summary>
public sealed record WasmPageItem(string Id, int Index, string ContentUri);

/// <summary>
/// Generic operation result returned from invoke envelope.
/// </summary>
public sealed record WasmOperationResult(bool IsError, string? Error, string? ContentType, string? PayloadJson);

/// <summary>
/// Represents a video stream returned from a WASM plugin.
/// </summary>
/// <param name="Id">The stream identifier.</param>
/// <param name="Label">The stream label.</param>
/// <param name="PlaylistUri">The playlist URI for the stream.</param>
/// <param name="RequestHeaders">Optional request headers required to fetch the stream.</param>
/// <param name="RequestCookies">Optional cookies required to fetch the stream.</param>
/// <param name="StreamType">The stream type, such as HLS or DASH.</param>
/// <param name="IsLive">Whether the stream is live.</param>
/// <param name="DrmProtected">Whether the stream is DRM-protected.</param>
/// <param name="DrmScheme">The DRM scheme identifier.</param>
/// <param name="AudioTracks">The available audio tracks.</param>
/// <param name="SubtitleTracks">The available subtitle tracks.</param>
/// <param name="DefaultAudioTrackId">The default audio track identifier.</param>
/// <param name="DefaultSubtitleTrackId">The default subtitle track identifier.</param>
public sealed record WasmVideoStreamItem(
    string Id,
    string Label,
    string PlaylistUri,
    IReadOnlyDictionary<string, string>? RequestHeaders = null,
    string? RequestCookies = null,
    string? StreamType = null,
    bool IsLive = false,
    bool DrmProtected = false,
    string? DrmScheme = null,
    IReadOnlyList<WasmVideoTrackItem>? AudioTracks = null,
    IReadOnlyList<WasmVideoTrackItem>? SubtitleTracks = null,
    string? DefaultAudioTrackId = null,
    string? DefaultSubtitleTrackId = null);

/// <summary>
/// Represents an audio or subtitle track returned with a WASM video stream.
/// </summary>
/// <param name="Id">The track identifier.</param>
/// <param name="Label">The track label.</param>
/// <param name="Language">The optional language tag.</param>
/// <param name="Codec">The optional codec identifier.</param>
/// <param name="IsDefault">Whether the track is selected by default.</param>
public sealed record WasmVideoTrackItem(
    string Id,
    string Label,
    string? Language = null,
    string? Codec = null,
    bool IsDefault = false);

/// <summary>
/// Represents the JSON wire payload for a returned video segment.
/// </summary>
/// <param name="ContentType">The segment content type.</param>
/// <param name="PayloadBase64">The base64-encoded segment payload.</param>
public sealed record WasmVideoSegmentWire(
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("payload")] string PayloadBase64);

/// <summary>
/// Represents a decoded video segment returned by the WASM runtime host.
/// </summary>
/// <param name="ContentType">The segment content type.</param>
/// <param name="Payload">The binary segment payload.</param>
public sealed record WasmVideoSegmentResult(string ContentType, byte[] Payload);

/// <summary>
/// Structured capability item returned by typed WASM components.
/// </summary>
public sealed record WasmCapabilityItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mediaTypes")] IReadOnlyList<string> MediaTypes,
    [property: JsonPropertyName("operations")] IReadOnlyList<string> Operations);

/// <summary>
/// Query args for invoke operations.
/// </summary>
public sealed record WasmQueryArgs(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("mediaTypes")] IReadOnlyList<string>? MediaTypes = null,
    [property: JsonPropertyName("filters")] IReadOnlyList<WasmSearchFilterArg>? Filters = null,
    [property: JsonPropertyName("queryAdditions")] IReadOnlyList<WasmSearchQueryAdditionArg>? QueryAdditions = null,
    [property: JsonPropertyName("sort")] string? Sort = null,
    [property: JsonPropertyName("page")] int? Page = null,
    [property: JsonPropertyName("pageSize")] int? PageSize = null);

/// <summary>
/// Query args for lookup-backed search suggestion operations.
/// </summary>
public sealed record WasmSearchSuggestionsArgs(
    [property: JsonPropertyName("controlId")] string ControlId,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("searchQuery")] WasmQueryArgs? SearchQuery = null,
    [property: JsonPropertyName("limit")] int? Limit = null);

/// <summary>
/// Represents a lookup suggestion returned by a WASM plugin.
/// </summary>
public sealed record WasmSearchSuggestionItem(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("description")] string? Description = null);

/// <summary>
/// Represents a structured search filter argument passed to a WASM plugin.
/// </summary>
/// <param name="Id">The filter identifier.</param>
/// <param name="Values">The selected filter values.</param>
/// <param name="Operation">The optional filter operation.</param>
public sealed record WasmSearchFilterArg(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("values")] IReadOnlyList<string> Values,
    [property: JsonPropertyName("operation")] string? Operation = null);

/// <summary>
/// Represents a structured query addition argument passed to a WASM plugin.
/// </summary>
/// <param name="Id">The addition identifier.</param>
/// <param name="Value">The submitted value.</param>
/// <param name="Type">The optional addition type.</param>
public sealed record WasmSearchQueryAdditionArg(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("type")] string? Type = null);

/// <summary>
/// Arguments for on-demand search metadata enrichment.
/// </summary>
public sealed record WasmEnrichMediaArgs(
    [property: JsonPropertyName("itemIds")] IReadOnlyList<string> ItemIds,
    [property: JsonPropertyName("baseItems")] IReadOnlyList<WasmSearchItem>? BaseItems = null);

/// <summary>
/// Benchmark args for invoke operations.
/// </summary>
public sealed record WasmBenchmarkArgs([property: JsonPropertyName("iterations")] int Iterations);

/// <summary>
/// Single page args for invoke operations.
/// </summary>
public sealed record WasmPageArgs(
    [property: JsonPropertyName("chapterId")] string ChapterId,
    [property: JsonPropertyName("pageIndex")] int PageIndex);

/// <summary>
/// Batch pages args for invoke operations.
/// </summary>
public sealed record WasmPagesArgs(
    [property: JsonPropertyName("chapterId")] string ChapterId,
    [property: JsonPropertyName("startIndex")] int StartIndex,
    [property: JsonPropertyName("count")] int Count);

/// <summary>
/// Arguments used to request a specific video segment from a WASM plugin.
/// </summary>
/// <param name="StreamId">The stream identifier.</param>
/// <param name="Sequence">The segment sequence number.</param>
public sealed record WasmVideoSegmentArgs(
    [property: JsonPropertyName("streamId")] string StreamId,
    [property: JsonPropertyName("sequence")] int Sequence);
