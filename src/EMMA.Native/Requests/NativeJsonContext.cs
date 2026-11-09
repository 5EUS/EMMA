using System.Text.Json.Serialization;

namespace EMMA.Native.Requests;

/// <summary>
/// Source-generated JSON for the requests only the native library has. Results use the shared
/// <c>ApiJsonContext</c>. Names are <c>snake_case</c> and a field the operation does not define is refused.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PluginCall))]
[JsonSerializable(typeof(SearchCall))]
[JsonSerializable(typeof(PageCall))]
[JsonSerializable(typeof(RefCall))]
[JsonSerializable(typeof(RefsCall))]
[JsonSerializable(typeof(RangeCall))]
[JsonSerializable(typeof(AssetCall))]
[JsonSerializable(typeof(ProgressCall))]
[JsonSerializable(typeof(RecentCall))]
[JsonSerializable(typeof(InstallCall))]
[JsonSerializable(typeof(StatusCall))]
[JsonSerializable(typeof(SettingsCall))]
[JsonSerializable(typeof(LibraryRefCall))]
[JsonSerializable(typeof(LibraryListCall))]
[JsonSerializable(typeof(LibrarySearchCall))]
[JsonSerializable(typeof(LibraryNameCall))]
[JsonSerializable(typeof(RetentionCall))]
[JsonSerializable(typeof(AppSettingsCall))]
[JsonSerializable(typeof(DownloadEnqueueCall))]
[JsonSerializable(typeof(DownloadIdCall))]
[JsonSerializable(typeof(DownloadListCall))]
[JsonSerializable(typeof(DownloadStatesCall))]
[JsonSerializable(typeof(DownloadSettingsCall))]
[JsonSerializable(typeof(ContentInfo))]
internal sealed partial class NativeJsonContext : JsonSerializerContext
{
}
