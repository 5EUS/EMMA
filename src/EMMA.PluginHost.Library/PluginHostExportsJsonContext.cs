using System.Text.Json;
using System.Text.Json.Serialization;
using EMMA.Domain;
using EMMA.PluginHost.Plugins;

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
[JsonSerializable(typeof(MediaChapter))]
[JsonSerializable(typeof(IReadOnlyList<MediaChapter>))]
[JsonSerializable(typeof(List<MediaChapter>))]
[JsonSerializable(typeof(MediaPage))]
[JsonSerializable(typeof(MediaPagesResult))]
[JsonSerializable(typeof(MediaPageAsset))]
[JsonSerializable(typeof(PluginSummaryResponse))]
[JsonSerializable(typeof(List<PluginSummaryResponse>))]
[JsonSerializable(typeof(PluginManifestSearchExperience))]
[JsonSerializable(typeof(PluginManifestSearchLanding))]
[JsonSerializable(typeof(PluginManifestSearchLandingSection))]
[JsonSerializable(typeof(PluginManifestSearchLandingAction))]
[JsonSerializable(typeof(PluginManifestSearchFilter))]
[JsonSerializable(typeof(PluginManifestSearchFilterRange))]
[JsonSerializable(typeof(PluginManifestSearchFilterOption))]
[JsonSerializable(typeof(PluginManifestSearchQueryOptions))]
[JsonSerializable(typeof(PluginManifestSearchQueryAddition))]
[JsonSerializable(typeof(MediaProgressResponse))]
[JsonSerializable(typeof(HistoryEntryResponse))]
[JsonSerializable(typeof(IReadOnlyList<HistoryEntryResponse>))]
[JsonSerializable(typeof(List<HistoryEntryResponse>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<string>))]
public partial class PluginHostExportsJsonContext : JsonSerializerContext
{
}
