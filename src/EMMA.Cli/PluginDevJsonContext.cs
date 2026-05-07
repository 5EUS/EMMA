using System.Text.Json;
using System.Text.Json.Serialization;
using EMMA.Plugin.Common;

namespace EMMA.Cli;

[JsonSerializable(typeof(PluginDevConfigDocument))]
[JsonSerializable(typeof(PluginDevProfileDocument))]
[JsonSerializable(typeof(PluginDevLoggingDocument))]
[JsonSerializable(typeof(PluginDevSyncDocument))]
[JsonSerializable(typeof(SearchItem[]))]
[JsonSerializable(typeof(ChapterItem[]))]
[JsonSerializable(typeof(PageItem))]
[JsonSerializable(typeof(PageItem[]))]
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