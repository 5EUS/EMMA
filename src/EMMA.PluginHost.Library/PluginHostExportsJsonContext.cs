using System.Text.Json;
using System.Text.Json.Serialization;
using EMMA.Domain;
using EMMA.Plugin.Common;
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
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(MediaSummary))]
[JsonSerializable(typeof(IReadOnlyList<MediaSummary>))]
[JsonSerializable(typeof(List<MediaSummary>))]
[JsonSerializable(typeof(SearchItem))]
[JsonSerializable(typeof(IReadOnlyList<SearchItem>))]
[JsonSerializable(typeof(List<SearchItem>))]
[JsonSerializable(typeof(SearchSuggestionRequest))]
[JsonSerializable(typeof(SearchSuggestionItem))]
[JsonSerializable(typeof(IReadOnlyList<SearchSuggestionItem>))]
[JsonSerializable(typeof(List<SearchSuggestionItem>))]
[JsonSerializable(typeof(PluginDevEnrichSearchItemsRequest))]
[JsonSerializable(typeof(PluginHostExports.EnrichedMediaSummaryResponse))]
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
[JsonSerializable(typeof(DownloadJobResponse))]
[JsonSerializable(typeof(IReadOnlyList<DownloadJobResponse>))]
[JsonSerializable(typeof(List<DownloadJobResponse>))]
[JsonSerializable(typeof(PluginSummaryResponse))]
[JsonSerializable(typeof(List<PluginSummaryResponse>))]
[JsonSerializable(typeof(PluginManifestSearchExperience))]
[JsonSerializable(typeof(PluginManifestSearchLanding))]
[JsonSerializable(typeof(PluginManifestSearchLandingSection))]
[JsonSerializable(typeof(PluginManifestSearchLandingAction))]
[JsonSerializable(typeof(PluginManifestSearchFilter))]
[JsonSerializable(typeof(PluginManifestSearchFilterRange))]
[JsonSerializable(typeof(PluginManifestSearchLookup))]
[JsonSerializable(typeof(PluginManifestSearchFilterOption))]
[JsonSerializable(typeof(PluginManifestSearchQueryOptions))]
[JsonSerializable(typeof(PluginManifestSearchQueryAddition))]
[JsonSerializable(typeof(MediaProgressResponse))]
[JsonSerializable(typeof(MigrationOrphanedItemResponse))]
[JsonSerializable(typeof(IReadOnlyList<MigrationOrphanedItemResponse>))]
[JsonSerializable(typeof(List<MigrationOrphanedItemResponse>))]
[JsonSerializable(typeof(MigrationMatchResponse))]
[JsonSerializable(typeof(IReadOnlyList<MigrationMatchResponse>))]
[JsonSerializable(typeof(List<MigrationMatchResponse>))]
[JsonSerializable(typeof(MigrationCheckRequest))]
[JsonSerializable(typeof(MigrationCheckResponse))]
[JsonSerializable(typeof(MigrationExecutionRequest))]
[JsonSerializable(typeof(MigrationExecutionResponse))]
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
