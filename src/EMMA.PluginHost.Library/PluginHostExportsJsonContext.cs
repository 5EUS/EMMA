using System.Text.Json;
using System.Text.Json.Serialization;
using EMMA.Domain;

namespace EMMA.PluginHost.Library;

/// <summary>
/// Source-generated JSON serialization context for types used by PluginHostExports.
/// Required for NativeAOT compatibility where reflection-based serialization is disabled.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(MediaSummary))]
[JsonSerializable(typeof(IReadOnlyList<MediaSummary>))]
[JsonSerializable(typeof(List<MediaSummary>))]
[JsonSerializable(typeof(PluginSummaryResponse))]
[JsonSerializable(typeof(List<PluginSummaryResponse>))]
public partial class PluginHostExportsJsonContext : JsonSerializerContext
{
}
