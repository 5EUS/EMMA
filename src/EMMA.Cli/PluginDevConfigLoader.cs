using System.Text.Json;
using System.Text.Json.Nodes;
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

        try
        {
            var content = File.ReadAllText(configPath);
            var document = JsonSerializer.Deserialize(content, PluginDevJsonContexts.Config.PluginDevConfigDocument)
                ?? new PluginDevConfigDocument();
            var sidecarUi = LoadUiState(configPath);
            document = document with
            {
                Ui = sidecarUi ?? document.Ui,
                Scenarios = LoadScenarioFiles(document, configPath)
            };

            return new PluginDevConfigLoadResult(configPath, document);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(BuildConfigLoadErrorMessage(configPath, ex.Message, ex.LineNumber, ex.BytePositionInLine));
        }
        catch (InvalidOperationException ex) when (!string.IsNullOrWhiteSpace(configPath))
        {
            throw new InvalidOperationException(BuildConfigLoadErrorMessage(configPath, ex.Message, null, null), ex);
        }
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

    private static string BuildConfigLoadErrorMessage(string configPath, string detail, long? lineNumber, long? bytePositionInLine)
    {
        var location = lineNumber is not null && bytePositionInLine is not null
            ? $" (line {lineNumber.Value + 1}, byte {bytePositionInLine.Value + 1})"
            : string.Empty;
        return $"Failed to load plugin development config '{configPath}'{location}. {detail}\n"
            + "Check JSON syntax, scenario step shapes, scenario files, and any custom config blocks such as 'profiles', 'sync', and 'scenariosPath'.";
    }

    private static Dictionary<string, PluginDevScenarioDocument>? LoadScenarioFiles(PluginDevConfigDocument document, string configPath)
    {
        var scenarioDirectory = ResolveScenarioDirectory(document, configPath);
        if (string.IsNullOrWhiteSpace(scenarioDirectory) || !Directory.Exists(scenarioDirectory))
        {
            return document.Scenarios;
        }

        var scenarios = document.Scenarios is null
            ? new Dictionary<string, PluginDevScenarioDocument>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PluginDevScenarioDocument>(document.Scenarios, StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in Directory.EnumerateFiles(scenarioDirectory, "*.json", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var content = File.ReadAllText(filePath);
                var scenario = JsonSerializer.Deserialize(content, PluginDevJsonContexts.Config.PluginDevScenarioFileDocument)
                    ?? new PluginDevScenarioFileDocument();
                var scenarioName = ResolveScenarioName(filePath, scenario.Name);
                scenarios[scenarioName] = new PluginDevScenarioDocument
                {
                    DisplayName = scenario.DisplayName,
                    Description = scenario.Description,
                    DefaultQuery = scenario.DefaultQuery,
                    SupportsQuery = scenario.SupportsQuery,
                    QueryLabel = scenario.QueryLabel,
                    Profiles = scenario.Profiles,
                    Steps = scenario.Steps
                };
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(BuildConfigLoadErrorMessage(filePath, ex.Message, ex.LineNumber, ex.BytePositionInLine));
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(BuildConfigLoadErrorMessage(filePath, ex.Message, null, null), ex);
            }
        }

        return scenarios;
    }

    private static string? ResolveScenarioDirectory(PluginDevConfigDocument document, string configPath)
    {
        if (!string.IsNullOrWhiteSpace(document.ScenariosPath))
        {
            var baseDirectory = Path.GetDirectoryName(configPath) ?? Directory.GetCurrentDirectory();
            return Path.GetFullPath(Path.Combine(baseDirectory, document.ScenariosPath));
        }

        var defaultDirectory = Path.Combine(Path.GetDirectoryName(configPath) ?? Directory.GetCurrentDirectory(), "scenarios");
        return Directory.Exists(defaultDirectory) ? defaultDirectory : null;
    }

    private static string ResolveScenarioName(string filePath, string? explicitName)
    {
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return explicitName.Trim();
        }

        var fileName = Path.GetFileNameWithoutExtension(filePath);
        return fileName.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".scenario".Length]
            : fileName;
    }

    public void UpdateUiDiagnosticsLevel(string configPath, string diagnosticsLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticsLevel);

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Plugin development config was not found: {configPath}", configPath);
        }

        var uiStatePath = ResolveUiStatePath(configPath);
        JsonNode root;
        try
        {
            var content = File.Exists(uiStatePath)
                ? File.ReadAllText(uiStatePath)
                : "{}";
            root = JsonNode.Parse(content, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(BuildConfigLoadErrorMessage(uiStatePath, ex.Message, ex.LineNumber, ex.BytePositionInLine));
        }

        if (root is not JsonObject rootObject)
        {
            throw new InvalidOperationException($"Failed to load plugin development UI state '{uiStatePath}'. The root JSON value must be an object.");
        }

        rootObject["diagnosticsLevel"] = diagnosticsLevel;

        var existingUiState = LoadUiState(configPath);
        if (existingUiState?.StartWatchByDefault is not null)
        {
            rootObject["startWatchByDefault"] = existingUiState.StartWatchByDefault.Value;
        }

        if (existingUiState?.StartServeByDefault is not null)
        {
            rootObject["startServeByDefault"] = existingUiState.StartServeByDefault.Value;
        }

        var uiStateDirectory = Path.GetDirectoryName(uiStatePath);
        if (!string.IsNullOrWhiteSpace(uiStateDirectory))
        {
            Directory.CreateDirectory(uiStateDirectory);
        }

        File.WriteAllText(
            uiStatePath,
            rootObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static PluginDevUiDocument? LoadUiState(string configPath)
    {
        var uiStatePath = ResolveUiStatePath(configPath);
        if (!File.Exists(uiStatePath))
        {
            return null;
        }

        try
        {
            var content = File.ReadAllText(uiStatePath);
            return JsonSerializer.Deserialize(content, PluginDevJsonContexts.Config.PluginDevUiDocument);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(BuildConfigLoadErrorMessage(uiStatePath, ex.Message, ex.LineNumber, ex.BytePositionInLine));
        }
    }

    private static string ResolveUiStatePath(string configPath)
    {
        var configDirectory = Path.GetDirectoryName(configPath) ?? Directory.GetCurrentDirectory();
        var configFileName = Path.GetFileNameWithoutExtension(configPath);
        return Path.Combine(configDirectory, "artifacts", "config", configFileName + ".ui.json");
    }
}

public sealed record PluginDevConfigLoadResult(string? ConfigPath, PluginDevConfigDocument Document);

public sealed record PluginDevConfigDocument
{
    [JsonPropertyName("defaultProfile")]
    public string? DefaultProfile { get; init; }

    [JsonPropertyName("ui")]
    public PluginDevUiDocument? Ui { get; init; }

    [JsonPropertyName("profiles")]
    public Dictionary<string, PluginDevProfileDocument>? Profiles { get; init; }

    [JsonPropertyName("scenarios")]
    public Dictionary<string, PluginDevScenarioDocument>? Scenarios { get; init; }

    [JsonPropertyName("scenariosPath")]
    public string? ScenariosPath { get; init; }
}

public sealed class PluginDevUiDocument
{
    [JsonPropertyName("diagnosticsLevel")]
    public string? DiagnosticsLevel { get; init; }

    [JsonPropertyName("startWatchByDefault")]
    public bool? StartWatchByDefault { get; init; }

    [JsonPropertyName("startServeByDefault")]
    public bool? StartServeByDefault { get; init; }
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

public sealed class PluginDevScenarioDocument
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("defaultQuery")]
    public string? DefaultQuery { get; set; }

    [JsonPropertyName("supportsQuery")]
    public bool? SupportsQuery { get; set; }

    [JsonPropertyName("queryLabel")]
    public string? QueryLabel { get; set; }

    [JsonPropertyName("profiles")]
    public List<string>? Profiles { get; set; }

    [JsonPropertyName("steps")]
    public List<PluginDevScenarioStepDocument>? Steps { get; set; }
}

public sealed class PluginDevScenarioStepDocument
{
    [JsonPropertyName("op")]
    public string? Op { get; set; }

    [JsonPropertyName("save")]
    public string? Save { get; set; }

    [JsonPropertyName("noWarn")]
    public JsonElement? NoWarn { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Parameters { get; set; }
}

public sealed class PluginDevScenarioFileDocument
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("defaultQuery")]
    public string? DefaultQuery { get; set; }

    [JsonPropertyName("supportsQuery")]
    public bool? SupportsQuery { get; set; }

    [JsonPropertyName("queryLabel")]
    public string? QueryLabel { get; set; }

    [JsonPropertyName("profiles")]
    public List<string>? Profiles { get; set; }

    [JsonPropertyName("steps")]
    public List<PluginDevScenarioStepDocument>? Steps { get; set; }
}