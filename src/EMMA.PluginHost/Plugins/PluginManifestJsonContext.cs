using System.Text.Json;
using System.Text.Json.Serialization;

namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Source-generated JSON serialization context for PluginManifest and related types.
/// Required for NativeAOT compatibility where reflection-based serialization is disabled.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginManifestRuntime))]
[JsonSerializable(typeof(PluginManifestWasmHostBridge))]
[JsonSerializable(typeof(PluginManifestWasmHttpOperation))]
[JsonSerializable(typeof(PluginManifestCapabilities))]
[JsonSerializable(typeof(PluginManifestPermissions))]
[JsonSerializable(typeof(PluginManifestSignature))]
[JsonSerializable(typeof(List<PluginManifest>))]
[JsonSerializable(typeof(Dictionary<string, PluginManifestWasmHttpOperation>))]
public partial class PluginManifestJsonContext : JsonSerializerContext
{
}
