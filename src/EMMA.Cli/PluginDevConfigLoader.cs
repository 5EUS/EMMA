using System.Text.Json;
using System.Text.Json.Serialization;

namespace EMMA.Cli;

public sealed class PluginDevConfigLoader
{
    public PluginDevConfigLoadResult Load(string workingDirectory)
    {
        var explicitConfigPath = Environment.GetEnvironmentVariable("EMMA_PLUGIN_DEV_CONFIG")?.Trim();
        var configPath = ResolveConfigPath(workingDirectory, explicitConfigPath);

        if (configPath is null)
        {
            return new PluginDevConfigLoadResult(null, new PluginDevConfigDocument());
        }

        var content = File.ReadAllText(configPath);
        var document = JsonSerializer.Deserialize(content, PluginDevJsonContexts.Config.PluginDevConfigDocument)
            ?? new PluginDevConfigDocument();

        return new PluginDevConfigLoadResult(configPath, document);
    }

    private static string? ResolveConfigPath(string workingDirectory, string? explicitConfigPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitConfigPath))
        {
            var candidate = Path.IsPathRooted(explicitConfigPath)
                ? explicitConfigPath
                : Path.GetFullPath(Path.Combine(workingDirectory, explicitConfigPath));

            if (!File.Exists(candidate))
            {
                throw new FileNotFoundException($"Plugin development config was not found: {candidate}", candidate);
            }

            return candidate;
        }

        var directory = new DirectoryInfo(workingDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "plugin.dev.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

public sealed record PluginDevConfigLoadResult(string? ConfigPath, PluginDevConfigDocument Document);

public sealed class PluginDevConfigDocument
{
    [JsonPropertyName("defaultProfile")]
    public string? DefaultProfile { get; init; }

    [JsonPropertyName("profiles")]
    public Dictionary<string, PluginDevProfileDocument>? Profiles { get; init; }
}

public sealed class PluginDevProfileDocument
{
    [JsonPropertyName("pluginId")]
    public string? PluginId { get; init; }

    [JsonPropertyName("hostUrl")]
    public string? HostUrl { get; init; }

    [JsonPropertyName("runtimeTarget")]
    public string? RuntimeTarget { get; init; }

    [JsonPropertyName("executionMode")]
    public string? ExecutionMode { get; init; }

    [JsonPropertyName("logging")]
    public PluginDevLoggingDocument? Logging { get; init; }

    [JsonPropertyName("sync")]
    public PluginDevSyncDocument? Sync { get; init; }

    [JsonPropertyName("wasiSdkPath")]
    public string? WasiSdkPath { get; init; }

    [JsonPropertyName("watchGlobs")]
    public List<string>? WatchGlobs { get; init; }
}

public sealed class PluginDevLoggingDocument
{
    [JsonPropertyName("plugin")]
    public bool? Plugin { get; init; }

    [JsonPropertyName("aspNetHost")]
    public bool? AspNetHost { get; init; }

    [JsonPropertyName("httpClient")]
    public bool? HttpClient { get; init; }
}

public sealed class PluginDevSyncDocument
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("destinationPath")]
    public string? DestinationPath { get; init; }

    [JsonPropertyName("onBuild")]
    public bool? OnBuild { get; init; }

    [JsonPropertyName("cleanDestination")]
    public bool? CleanDestination { get; init; }
}