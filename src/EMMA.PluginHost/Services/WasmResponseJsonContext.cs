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
public sealed record WasmChapterItem(string Id, int Number, string Title);

/// <summary>
/// Page item returned from WASM component.
/// </summary>
public sealed record WasmPageItem(string Id, int Index, string ContentUri);
