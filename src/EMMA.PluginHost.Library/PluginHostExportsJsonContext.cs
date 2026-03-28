using System.Text.Json;
using System.Text.Json.Serialization;
using EMMA.Domain;
using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Services;

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
[JsonSerializable(typeof(VideoStreamResponse))]
[JsonSerializable(typeof(IReadOnlyList<VideoStreamResponse>))]
[JsonSerializable(typeof(List<VideoStreamResponse>))]
[JsonSerializable(typeof(VideoStreamsResponse))]
[JsonSerializable(typeof(VideoSegmentAssetResponse))]
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
[JsonSerializable(typeof(PluginRepositoryRecord))]
[JsonSerializable(typeof(IReadOnlyList<PluginRepositoryRecord>))]
[JsonSerializable(typeof(List<PluginRepositoryRecord>))]
[JsonSerializable(typeof(PluginRepositoryPluginView))]
[JsonSerializable(typeof(IReadOnlyList<PluginRepositoryPluginView>))]
[JsonSerializable(typeof(List<PluginRepositoryPluginView>))]
[JsonSerializable(typeof(RepositoryPluginsResponse))]
[JsonSerializable(typeof(PluginRepositoryInstallResult))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(LibraryMediaRefreshFailure))]
[JsonSerializable(typeof(IReadOnlyList<LibraryMediaRefreshFailure>))]
[JsonSerializable(typeof(List<LibraryMediaRefreshFailure>))]
[JsonSerializable(typeof(LibraryMediaDiscoveredUpdate))]
[JsonSerializable(typeof(IReadOnlyList<LibraryMediaDiscoveredUpdate>))]
[JsonSerializable(typeof(List<LibraryMediaDiscoveredUpdate>))]
[JsonSerializable(typeof(LibraryMediaRefreshResponse))]
public partial class PluginHostExportsJsonContext : JsonSerializerContext
{
}
