using System.Text.Json;
using System.Text.Json.Serialization;

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
[JsonSerializable(typeof(WasmVideoSegmentWire))]
[JsonSerializable(typeof(WasmVideoSegmentArgs))]
[JsonSerializable(typeof(WasmCapabilityItem))]
[JsonSerializable(typeof(IReadOnlyList<WasmCapabilityItem>))]
[JsonSerializable(typeof(List<WasmCapabilityItem>))]
[JsonSerializable(typeof(WasmQueryArgs))]
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
public sealed record WasmSearchItem(string Id, string? Source, string Title, string? MediaType, string? ThumbnailUrl = null, string? Description = null);

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

public sealed record WasmVideoStreamItem(string Id, string Label, string PlaylistUri);

public sealed record WasmVideoSegmentWire(
	[property: JsonPropertyName("contentType")] string ContentType,
	[property: JsonPropertyName("payload")] string PayloadBase64);

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

public sealed record WasmSearchFilterArg(
	[property: JsonPropertyName("id")] string Id,
	[property: JsonPropertyName("values")] IReadOnlyList<string> Values,
	[property: JsonPropertyName("operation")] string? Operation = null);

public sealed record WasmSearchQueryAdditionArg(
	[property: JsonPropertyName("id")] string Id,
	[property: JsonPropertyName("value")] string Value,
	[property: JsonPropertyName("type")] string? Type = null);

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

public sealed record WasmVideoSegmentArgs(
	[property: JsonPropertyName("streamId")] string StreamId,
	[property: JsonPropertyName("sequence")] int Sequence);
