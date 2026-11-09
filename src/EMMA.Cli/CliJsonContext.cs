using System.Text.Json.Serialization;

using EMMA.Api.Contracts;

namespace EMMA.Cli;

/// <summary>Indented JSON for <c>--json</c> output: the same shapes the HTTP API and the native library use.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PluginListResponse))]
[JsonSerializable(typeof(PluginDetailResponse))]
[JsonSerializable(typeof(PluginSettingsResponse))]
[JsonSerializable(typeof(PluginSummary))]
[JsonSerializable(typeof(NodePageResponse))]
[JsonSerializable(typeof(NodeListResponse))]
[JsonSerializable(typeof(SuggestionsResponse))]
[JsonSerializable(typeof(AssetListResponse))]
[JsonSerializable(typeof(DownloadDto))]
[JsonSerializable(typeof(DownloadListResponse))]
[JsonSerializable(typeof(DownloadCountResponse))]
[JsonSerializable(typeof(DownloadSettingsResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class CliJsonContext : JsonSerializerContext
{
}
