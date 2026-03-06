using System.Text.Json;
using System.Text.Json.Serialization;

namespace EMMA.PluginHost.Services;

/// <summary>
/// Source-generated JSON serialization context for NativeInProcessWasmComponentInvoker.
/// Required for NativeAOT compatibility where reflection-based serialization is disabled.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false)]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
public partial class WasmComponentInvokerJsonContext : JsonSerializerContext
{
}
