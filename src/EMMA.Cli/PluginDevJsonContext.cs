using System.Text.Json;
using System.Text.Json.Serialization;
using EMMA.Plugin.Common;

namespace EMMA.Cli;

[JsonSerializable(typeof(PluginDevConfigDocument))]
[JsonSerializable(typeof(PluginDevUiDocument))]
[JsonSerializable(typeof(PluginDevProfileDocument))]
[JsonSerializable(typeof(PluginDevLoggingDocument))]
[JsonSerializable(typeof(PluginDevSyncDocument))]
[JsonSerializable(typeof(PluginDevScenarioDocument))]
[JsonSerializable(typeof(PluginDevScenarioFileDocument))]
[JsonSerializable(typeof(PluginDevScenarioStepDocument))]
[JsonSerializable(typeof(IReadOnlyList<SearchItem>))]
[JsonSerializable(typeof(SearchItem[]))]
[JsonSerializable(typeof(ChapterItem[]))]
[JsonSerializable(typeof(PageItem))]
[JsonSerializable(typeof(PageItem[]))]
[JsonSerializable(typeof(PluginDevVideoTrack[]))]
[JsonSerializable(typeof(PluginDevVideoStream[]))]
[JsonSerializable(typeof(PluginDevVideoSegment))]
internal sealed partial class PluginDevJsonContext : JsonSerializerContext
{
}

internal static class PluginDevJsonContexts
{
    private static readonly JsonSerializerOptions ConfigOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions RuntimeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static readonly PluginDevJsonContext Config = new(ConfigOptions);

    public static readonly PluginDevJsonContext Runtime = new(RuntimeOptions);
}