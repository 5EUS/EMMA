using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Globalization;
using System.Net;
using System.Net.Http;
using EMMA.Api;
using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;
using EMMA.PluginHost.Library;

namespace EMMA.Native;

public static partial class NativeExports
{
    private const string NativeConsoleLogLevelEnvVar = "EMMA_NATIVE_LOG_LEVEL";
    private const string NativeVerboseTimingEnvVar = "EMMA_NATIVE_VERBOSE_TIMING";
    private const string RequireSignedPluginsEnvVar = "EMMA_REQUIRE_SIGNED_PLUGINS";
    private const string PluginSignatureRequireSignedEnvVar = "PluginSignature__RequireSignedPlugins";
    private const string RequireSignedPluginsFileEnvVar = "EMMA_REQUIRE_SIGNED_PLUGINS_FILE";
    private const string RequireSignedPluginsManifestFileName = ".plugin-signature-require-signed";
    private const string RequireSignedPluginsAppFileName = "plugin-signature-require-signed";
    private const string PluginSignatureDelegationDirectoryEnvVar = "EMMA_PLUGIN_SIGNATURE_DELEGATION_DIR";
    private const string PluginSignatureDelegationDirectoryConfigEnvVar = "PluginSignature__DelegationDirectory";
    private const string PluginSignatureDelegationDirectoryFileEnvVar = "EMMA_PLUGIN_SIGNATURE_DELEGATION_DIR_FILE";
    private const string PluginSignatureDelegationDirectoryFileName = ".plugin-signature-delegation-dir";
    private const string PluginSignatureRootKeyDirectoryEnvVar = "EMMA_PLUGIN_SIGNATURE_ROOT_KEY_DIR";
    private const string PluginSignatureRootKeyDirectoryConfigEnvVar = "PluginSignature__RootKeyDirectory";
    private const string PluginSignatureRootKeyDirectoryFileEnvVar = "EMMA_PLUGIN_SIGNATURE_ROOT_KEY_DIR_FILE";
    private const string PluginSignatureRootKeyDirectoryFileName = ".plugin-signature-root-key-dir";
    private sealed class RuntimeState(EmbeddedRuntime runtime, InMemoryMediaStore store)
    {
        public EmbeddedRuntime Runtime { get; } = runtime;
        public InMemoryMediaStore Store { get; } = store;
        public string? SelectedPluginId { get; set; }
    }
    private sealed record PluginSummary(
        string Id,
        string Title,
        string Version,
        string BuildType,
        double? ThumbnailAspectRatio = null,
        string? ThumbnailFit = null,
        int? ThumbnailWidth = null,
        int? ThumbnailHeight = null,
        string? SearchExperienceJson = null);
    private sealed record PluginPathConfiguration(string? ManifestsDirectory, string? PluginsDirectory);
    private sealed record PluginHostConfiguration(string Mode, string? BaseUrl, string? ExecutablePath);

    private static readonly ConcurrentDictionary<int, RuntimeState> States = new();
    private static readonly Lock PluginHostInitLock = new();
    private static readonly Lock RuntimeLifecycleLock = new();
    private static readonly Lock ErrorLock = new();
    private static readonly NativeLogStore LogStore = new();
    private static int _nextHandle;
    private static int? _sharedRuntimeHandle;
    private static int _runtimeReferenceCount;
    private static PluginPathConfiguration _pluginPathConfiguration = new(null, null);
    private static bool _pluginHostInitialized = false;
    private static int _nativeLoggingConfigured = 0;
    private static readonly HttpClient RemotePluginHostHttpClient = new();
    private static PluginHostConfiguration _pluginHostConfiguration = new("local", null, null);

    // Don't use [ThreadStatic] - we need the error to be visible across threads for FFI
    private static string? _lastError;

    private static string? ResolveActivePluginId(RuntimeState state)
    {
        if (!string.IsNullOrWhiteSpace(state.SelectedPluginId))
        {
            return state.SelectedPluginId;
        }

        var discovered = DiscoverPlugins();
        var selected = discovered.FirstOrDefault()?.Id;
        state.SelectedPluginId = selected;
        return selected;
    }

    private readonly record struct SearchHostPhaseResult(
        string Json,
        long HostCallMs,
        string? HostTimingSnapshot,
        string CorrelationId);

    private static SearchHostPhaseResult SearchViaRemotePluginHostTimed(Uri baseUri, string pluginId, string query)
    {
        var correlationId = Guid.NewGuid().ToString("n");
        var hostCallStopwatch = Stopwatch.StartNew();
        var requestUri = BuildRequestUri(
            baseUri,
            $"/pipeline/paged/search?pluginId={Uri.EscapeDataString(pluginId)}&query={Uri.EscapeDataString(query)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var response = RemotePluginHostHttpClient.Send(request);
        var json = ReadHttpJsonBody(response, requestUri);
        hostCallStopwatch.Stop();
        return new SearchHostPhaseResult(json, hostCallStopwatch.ElapsedMilliseconds, null, correlationId);
    }

    private static SearchHostPhaseResult SearchViaEmbeddedPluginHostTimed(string pluginId, string query)
    {
        EnsurePluginHostInitialized();
        var correlationId = Guid.NewGuid().ToString("n");

        var hostCallStopwatch = Stopwatch.StartNew();
        var json = PluginHostExports.SearchJsonManaged(pluginId, query, correlationId);
        hostCallStopwatch.Stop();

        var hostTimingSnapshot = PluginHostExports.TakeLastSearchTimingManaged();
        if (ShouldLogVerboseTimingDetails() && !string.IsNullOrWhiteSpace(hostTimingSnapshot))
        {
            LogInfo("timing", hostTimingSnapshot!);
        }

        if (json == null)
        {
            var error = PluginHostExports.GetLastErrorManaged() ?? "Plugin host search returned null";
            throw new InvalidOperationException(error);
        }

        return new SearchHostPhaseResult(json, hostCallStopwatch.ElapsedMilliseconds, hostTimingSnapshot, correlationId);
    }

    private static string SearchSuggestionsViaRemotePluginHost(Uri baseUri, string pluginId, string requestJson)
    {
        var requestUri = BuildRequestUri(
            baseUri,
            $"/pipeline/paged/search/suggestions?pluginId={Uri.EscapeDataString(pluginId)}");
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };
        using var response = RemotePluginHostHttpClient.Send(request);
        return ReadHttpJsonBody(response, requestUri);
    }

    private static string SearchSuggestionsViaEmbeddedPluginHost(string pluginId, string requestJson)
    {
        EnsurePluginHostInitialized();

        var json = PluginHostExports.SearchSuggestionsJsonManaged(pluginId, requestJson, Guid.NewGuid().ToString("n"));
        if (json is null)
        {
            var error = PluginHostExports.GetLastErrorManaged() ?? "Plugin host suggestions returned null";
            throw new InvalidOperationException(error);
        }

        return json;
    }

    private static bool TryGetObjectProperty(JsonElement element, string propertyName, out JsonElement objectValue)
    {
        if (element.TryGetProperty(propertyName, out var direct)
            && direct.ValueKind == JsonValueKind.Object)
        {
            objectValue = direct;
            return true;
        }

        foreach (var candidate in element.EnumerateObject())
        {
            if (!string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                || candidate.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            objectValue = candidate.Value;
            return true;
        }

        objectValue = default;
        return false;
    }

    private static string DecorateOpenPluginError(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "Plugin could not be opened.";
        }

        if (error.Contains("signature-invalid", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Plugin manifest signature is missing", StringComparison.OrdinalIgnoreCase))
        {
            return error + " Unsigned local development plugins require the signature policy to be disabled in emmaui Security settings, or the plugin must be installed with a valid signature.";
        }

        return error;
    }

    private static void EnsurePluginHostInitialized()
    {
        if (TryGetRemotePluginHostBaseUri(out _))
        {
            // Remote mode proxies supported calls directly over HTTP.
            return;
        }

        if (_pluginHostInitialized)
        {
            return;
        }

        lock (PluginHostInitLock)
        {
            if (_pluginHostInitialized)
            {
                return;
            }

            var manifestDirectory = ResolveManifestDirectory() ?? string.Empty;
            var sandboxDirectory = ResolvePluginSandboxDirectory() ?? string.Empty;

            EnsureRequireSignedPluginsConfigured(manifestDirectory);
            EnsurePluginSignatureTrustConfigured(manifestDirectory);

            var resultCode = PluginHostExports.InitializeManaged(manifestDirectory, sandboxDirectory);

            if (resultCode != 0)
            {
                var error = PluginHostExports.GetLastErrorManaged()
                    ?? $"Plugin host initialization failed with code {resultCode}";
                throw new InvalidOperationException(error);
            }

            _pluginHostInitialized = true;
            LogInfo("plugin-host", "Embedded plugin host initialized.");
        }
    }

    private static bool EnsurePluginSignatureTrustConfigured(string? manifestDirectory)
    {
        var changed = false;

        var existingDelegationDirectory = Environment.GetEnvironmentVariable(PluginSignatureDelegationDirectoryEnvVar)
            ?? Environment.GetEnvironmentVariable(PluginSignatureDelegationDirectoryConfigEnvVar);
        var resolvedDelegationDirectory = ResolvePluginSignatureDelegationDirectoryFromFiles(manifestDirectory);

        if (!string.IsNullOrWhiteSpace(resolvedDelegationDirectory)
            && !string.Equals(existingDelegationDirectory?.Trim(), resolvedDelegationDirectory, StringComparison.Ordinal))
        {
            Environment.SetEnvironmentVariable(PluginSignatureDelegationDirectoryEnvVar, resolvedDelegationDirectory);
            Environment.SetEnvironmentVariable(PluginSignatureDelegationDirectoryConfigEnvVar, resolvedDelegationDirectory);
            changed = true;
        }

        var existingRootKeyDirectory = Environment.GetEnvironmentVariable(PluginSignatureRootKeyDirectoryEnvVar)
            ?? Environment.GetEnvironmentVariable(PluginSignatureRootKeyDirectoryConfigEnvVar);
        var resolvedRootKeyDirectory = ResolvePluginSignatureRootKeyDirectoryFromFiles(manifestDirectory);

        if (!string.IsNullOrWhiteSpace(resolvedRootKeyDirectory)
            && !string.Equals(existingRootKeyDirectory?.Trim(), resolvedRootKeyDirectory, StringComparison.Ordinal))
        {
            Environment.SetEnvironmentVariable(PluginSignatureRootKeyDirectoryEnvVar, resolvedRootKeyDirectory);
            Environment.SetEnvironmentVariable(PluginSignatureRootKeyDirectoryConfigEnvVar, resolvedRootKeyDirectory);
            changed = true;
        }

        if (changed)
        {
            LogInfo("plugin-host", "Loaded delegated plugin signature trust configuration.");
        }

        return changed;
    }

    private static bool EnsureRequireSignedPluginsConfigured(string? manifestDirectory)
    {
        var existing = Environment.GetEnvironmentVariable(RequireSignedPluginsEnvVar)
            ?? Environment.GetEnvironmentVariable(PluginSignatureRequireSignedEnvVar);

        var resolved = ResolveRequireSignedPluginsFromFiles(manifestDirectory);
        if (!resolved.HasValue)
        {
            return false;
        }

        var normalized = resolved.Value ? "true" : "false";
        if (string.Equals(existing?.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Environment.SetEnvironmentVariable(RequireSignedPluginsEnvVar, normalized);
        Environment.SetEnvironmentVariable(PluginSignatureRequireSignedEnvVar, normalized);
        LogInfo("plugin-host", $"Loaded require-signed-plugins policy from file: {normalized}.");
        return true;
    }

    private static bool? ResolveRequireSignedPluginsFromFiles(string? manifestDirectory)
    {
        var candidatePaths = new List<string>();

        var explicitPath = Environment.GetEnvironmentVariable(RequireSignedPluginsFileEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidatePaths.Add(explicitPath);
        }

        if (!string.IsNullOrWhiteSpace(manifestDirectory))
        {
            candidatePaths.Add(Path.Combine(manifestDirectory, RequireSignedPluginsManifestFileName));
        }

        var support = GetApplicationSupportDirectories().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(support))
        {
            candidatePaths.Add(Path.Combine(support, "emmaui", RequireSignedPluginsAppFileName));
        }

        foreach (var path in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                var value = File.ReadAllText(path).Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (TryParseBooleanLike(value, out var parsed))
                {
                    return parsed;
                }
            }
            catch
            {
                // ignored
            }
        }

        return null;
    }

    private static bool TryParseBooleanLike(string value, out bool parsed)
    {
        if (bool.TryParse(value, out parsed))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "yes":
            case "on":
                parsed = true;
                return true;
            case "0":
            case "no":
            case "off":
                parsed = false;
                return true;
            default:
                parsed = false;
                return false;
        }
    }

    private static string? ResolvePluginSignatureDelegationDirectoryFromFiles(string? manifestDirectory)
    {
        var candidatePaths = new List<string>();

        var explicitPath = Environment.GetEnvironmentVariable(PluginSignatureDelegationDirectoryFileEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidatePaths.Add(explicitPath);
        }

        if (!string.IsNullOrWhiteSpace(manifestDirectory))
        {
            candidatePaths.Add(Path.Combine(manifestDirectory, PluginSignatureDelegationDirectoryFileName));
            candidatePaths.Add(Path.Combine(manifestDirectory, "trust"));
            candidatePaths.Add(Path.Combine(manifestDirectory, "delegations"));

            var manifestParent = Path.GetDirectoryName(manifestDirectory);
            if (!string.IsNullOrWhiteSpace(manifestParent))
            {
                candidatePaths.Add(Path.Combine(manifestParent, "trust"));
                candidatePaths.Add(Path.Combine(manifestParent, "delegations"));
            }
        }

        var support = GetApplicationSupportDirectories().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(support))
        {
            candidatePaths.Add(Path.Combine(support, "emmaui", "plugin-signature-trust"));
            candidatePaths.Add(Path.Combine(support, "emmaui", "plugin-signature-delegations"));
        }

        foreach (var path in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                return path;
            }

            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var value = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
                {
                    return value;
                }
            }
            catch
            {
                // ignored
            }
        }

        return null;
    }

    private static string? ResolvePluginSignatureRootKeyDirectoryFromFiles(string? manifestDirectory)
    {
        var candidatePaths = new List<string>();

        var explicitPath = Environment.GetEnvironmentVariable(PluginSignatureRootKeyDirectoryFileEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidatePaths.Add(explicitPath);
        }

        if (!string.IsNullOrWhiteSpace(manifestDirectory))
        {
            candidatePaths.Add(Path.Combine(manifestDirectory, PluginSignatureRootKeyDirectoryFileName));
            candidatePaths.Add(Path.Combine(manifestDirectory, "trust", "roots"));

            var manifestParent = Path.GetDirectoryName(manifestDirectory);
            if (!string.IsNullOrWhiteSpace(manifestParent))
            {
                candidatePaths.Add(Path.Combine(manifestParent, "trust", "roots"));
            }
        }

        var support = GetApplicationSupportDirectories().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(support))
        {
            candidatePaths.Add(Path.Combine(support, "emmaui", "plugin-signature-trust", "roots"));
            candidatePaths.Add(Path.Combine(support, "emmaui", "plugin-signature-root-keys"));
        }

        foreach (var path in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                return path;
            }

            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var value = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
                {
                    return value;
                }
            }
            catch
            {
                // ignored
            }
        }

        return null;
    }

    private static void ShutdownPluginHost()
    {
        lock (PluginHostInitLock)
        {
            if (!_pluginHostInitialized)
            {
                return;
            }

            PluginHostExports.ShutdownManaged();
            _pluginHostInitialized = false;
            LogInfo("plugin-host", "Embedded plugin host shutdown.");
        }
    }

    private static bool TryGetRemotePluginHostBaseUri(out Uri baseUri)
    {
        baseUri = default!;
        var config = _pluginHostConfiguration;
        if (!string.Equals(config.Mode, "remote", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            return false;
        }

        if (Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var parsed)
            && parsed is not null)
        {
            baseUri = parsed;
            return true;
        }

        return false;
    }

    private static string HttpGetJson(Uri baseUri, string relativePath)
    {
        var requestUri = BuildRequestUri(baseUri, relativePath);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var response = RemotePluginHostHttpClient.Send(request);
        return ReadHttpJsonBody(response, requestUri);
    }

    private static string HttpSendJson(Uri baseUri, string relativePath, HttpMethod method, string? jsonBody)
    {
        var requestUri = BuildRequestUri(baseUri, relativePath);
        using var request = new HttpRequestMessage(method, requestUri);
        if (!string.IsNullOrWhiteSpace(jsonBody))
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var response = RemotePluginHostHttpClient.Send(request);
        return ReadHttpJsonBody(response, requestUri);
    }

    private static Uri BuildRequestUri(Uri baseUri, string relativePath)
    {
        var normalizedBase = baseUri.ToString().TrimEnd('/') + "/";
        var normalizedPath = relativePath.TrimStart('/');
        return new Uri(new Uri(normalizedBase, UriKind.Absolute), normalizedPath);
    }

    private static string ReadHttpJsonBody(HttpResponseMessage response, Uri requestUri)
    {
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (response.IsSuccessStatusCode)
        {
            return string.IsNullOrWhiteSpace(body) ? "{}" : body;
        }

        var details = string.IsNullOrWhiteSpace(body)
            ? "<empty response>"
            : body;
        throw new HttpRequestException(
            $"Remote plugin host request failed ({(int)response.StatusCode} {response.StatusCode}) at {requestUri}: {details}",
            null,
            response.StatusCode);
    }

    private static string NormalizeRemotePluginsPayload(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return "[]";
            }

            var mapped = new List<PluginSummary>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = ReadJsonString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var loaded = ReadJsonBool(item, "loaded");
                if (loaded is false)
                {
                    continue;
                }

                var thumbnailAspectRatio = ReadJsonDouble(item, "thumbnailAspectRatio");
                var thumbnailFit = ReadJsonString(item, "thumbnailFit");
                var thumbnailWidth = ReadJsonInt(item, "thumbnailWidth");
                var thumbnailHeight = ReadJsonInt(item, "thumbnailHeight");

                if (TryGetJsonProperty(item, "thumbnail", out var thumbnail)
                    && thumbnail.ValueKind == JsonValueKind.Object)
                {
                    thumbnailAspectRatio ??= ReadJsonDouble(thumbnail, "aspectRatio");
                    thumbnailFit ??= ReadJsonString(thumbnail, "fit");
                    thumbnailWidth ??= ReadJsonInt(thumbnail, "width");
                    thumbnailHeight ??= ReadJsonInt(thumbnail, "height");
                }

                if ((thumbnailAspectRatio is null || thumbnailAspectRatio <= 0)
                    && thumbnailWidth is { } width
                    && thumbnailHeight is { } height
                    && width > 0
                    && height > 0)
                {
                    thumbnailAspectRatio = (double)width / height;
                }

                string? searchExperienceJson = null;
                if (TryGetJsonProperty(item, "searchExperience", out var searchExperience)
                    && searchExperience.ValueKind == JsonValueKind.Object)
                {
                    searchExperienceJson = searchExperience.GetRawText();
                }

                mapped.Add(new PluginSummary(
                    id,
                    ReadJsonString(item, "title")
                        ?? ReadJsonString(item, "name")
                        ?? id,
                    ReadJsonString(item, "version") ?? string.Empty,
                    ReadJsonString(item, "buildType") ?? "csharp",
                    thumbnailAspectRatio,
                    thumbnailFit,
                    thumbnailWidth,
                        thumbnailHeight,
                        searchExperienceJson));
            }

            return BuildPluginsJson(mapped);
        }
        catch
        {
            return "[]";
        }
    }

    private static string BuildAddPluginRepositoryPayload(
        string catalogUrl,
        string? repositoryId,
        string? name,
        string? sourceRepositoryUrl)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        AppendJsonProperty(sb, "catalogUrl", catalogUrl);
        sb.Append(',');
        AppendJsonNullableStringProperty(sb, "repositoryId", repositoryId);
        sb.Append(',');
        AppendJsonNullableStringProperty(sb, "name", name);
        sb.Append(',');
        AppendJsonNullableStringProperty(sb, "sourceRepositoryUrl", sourceRepositoryUrl);
        sb.Append('}');
        return sb.ToString();
    }

    private static string BuildInstallFromRepositoryPayload(
        string repositoryId,
        string pluginId,
        string? version,
        bool refreshCatalog,
        bool rescanAfterInstall)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        AppendJsonProperty(sb, "repositoryId", repositoryId);
        sb.Append(',');
        AppendJsonProperty(sb, "pluginId", pluginId);
        sb.Append(',');
        AppendJsonNullableStringProperty(sb, "version", version);
        sb.Append(',');
        AppendJsonBooleanProperty(sb, "refreshCatalog", refreshCatalog);
        sb.Append(',');
        AppendJsonBooleanProperty(sb, "rescanAfterInstall", rescanAfterInstall);
        sb.Append('}');
        return sb.ToString();
    }

    private static string? ReadJsonString(JsonElement element, string propertyName)
    {
        if (!TryGetJsonProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int? ReadJsonInt(JsonElement element, string propertyName)
    {
        if (!TryGetJsonProperty(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt32(out var intValue))
            {
                return intValue;
            }

            if (value.TryGetDouble(out var doubleValue))
            {
                return (int)doubleValue;
            }
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? ReadJsonDouble(JsonElement element, string propertyName)
    {
        if (!TryGetJsonProperty(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var numeric))
        {
            return numeric;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool TryGetJsonProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var item in element.EnumerateObject())
        {
            if (string.Equals(item.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = item.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool? ReadJsonBool(JsonElement element, string propertyName)
    {
        if (!TryGetJsonProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static bool ShouldLogVerboseTimingDetails()
    {
        var value = Environment.GetEnvironmentVariable(NativeVerboseTimingEnvVar)
            ?? Environment.GetEnvironmentVariable("EMMA_WASM_PAYLOAD_DIAGNOSTICS")
            ?? Environment.GetEnvironmentVariable("EMMA_PLUGIN_TIMING_DIAGNOSTICS");

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (bool.TryParse(value, out var parsedBool))
        {
            return parsedBool;
        }

        return value.Trim() is "1" or "yes" or "on";
    }

    private static void EnsureNativeLoggingConfigured()
    {
        if (Interlocked.Exchange(ref _nativeLoggingConfigured, 1) != 0)
        {
            return;
        }

        var configuredLevel = ResolveNativeConsoleLogLevel();
        LogStore.SetConsoleMinLevel(configuredLevel);
    }

    private static NativeLogLevel ResolveNativeConsoleLogLevel()
    {
        var value = Environment.GetEnvironmentVariable(NativeConsoleLogLevelEnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return NativeLogLevel.Information;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "trace" => NativeLogLevel.Trace,
            "debug" => NativeLogLevel.Debug,
            "information" or "info" => NativeLogLevel.Information,
            "warning" or "warn" => NativeLogLevel.Warning,
            "error" => NativeLogLevel.Error,
            _ => NativeLogLevel.Information
        };
    }

    private static string? ResolveManifestDirectory()
    {
        var explicitManifestDir = Environment.GetEnvironmentVariable("EMMA_MANIFESTS_DIR");
        if (!string.IsNullOrWhiteSpace(explicitManifestDir))
        {
            return explicitManifestDir;
        }

        if (!string.IsNullOrWhiteSpace(_pluginPathConfiguration.ManifestsDirectory))
        {
            return _pluginPathConfiguration.ManifestsDirectory;
        }

        var support = GetApplicationSupportDirectories().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(support))
        {
            return Path.Combine(support, "emmaui", "manifests");
        }

        return null;
    }

    private static string? ResolvePluginSandboxDirectory()
    {
        var explicitPluginsDir = Environment.GetEnvironmentVariable("EMMA_PLUGINS_DIR");
        if (!string.IsNullOrWhiteSpace(explicitPluginsDir))
        {
            return explicitPluginsDir;
        }

        if (!string.IsNullOrWhiteSpace(_pluginPathConfiguration.PluginsDirectory))
        {
            return _pluginPathConfiguration.PluginsDirectory;
        }

        var support = GetApplicationSupportDirectories().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(support))
        {
            return Path.Combine(support, "emmaui", "plugins");
        }

        return null;
    }

    private static string BuildPluginsJson(IReadOnlyList<PluginSummary> plugins)
    {
        var sb = new StringBuilder();
        sb.Append('[');

        for (var i = 0; i < plugins.Count; i++)
        {
            var plugin = plugins[i];
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('{');
            AppendJsonProperty(sb, "id", plugin.Id);
            sb.Append(',');
            AppendJsonProperty(sb, "title", plugin.Title);
            sb.Append(',');
            AppendJsonProperty(sb, "version", plugin.Version);
            sb.Append(',');
            AppendJsonProperty(sb, "buildType", plugin.BuildType);
            if (plugin.ThumbnailAspectRatio is { } aspectRatio && aspectRatio > 0)
            {
                sb.Append(',');
                AppendJsonDoubleProperty(sb, "thumbnailAspectRatio", aspectRatio);
            }

            if (!string.IsNullOrWhiteSpace(plugin.ThumbnailFit))
            {
                sb.Append(',');
                AppendJsonProperty(sb, "thumbnailFit", plugin.ThumbnailFit!);
            }

            if (plugin.ThumbnailWidth is { } width && width > 0)
            {
                sb.Append(',');
                AppendJsonNumberProperty(sb, "thumbnailWidth", width);
            }

            if (plugin.ThumbnailHeight is { } height && height > 0)
            {
                sb.Append(',');
                AppendJsonNumberProperty(sb, "thumbnailHeight", height);
            }

            if (!string.IsNullOrWhiteSpace(plugin.SearchExperienceJson))
            {
                sb.Append(',');
                AppendJsonString(sb, "searchExperience");
                sb.Append(':');
                sb.Append(plugin.SearchExperienceJson);
            }
            sb.Append('}');
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static IReadOnlyList<PluginSummary> DiscoverPlugins()
    {
        var byId = new Dictionary<string, PluginSummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifestDirectory in GetManifestDirectories())
        {
            if (!Directory.Exists(manifestDirectory))
            {
                continue;
            }

            IEnumerable<string> manifests;
            try
            {
                manifests = Directory.EnumerateFiles(manifestDirectory, "*.plugin.json", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var manifestPath in manifests)
            {
                var summary = TryParseManifest(manifestPath);
                if (summary is null)
                {
                    continue;
                }

                var buildType = ResolvePluginBuildType(summary.Id);
                byId[summary.Id] = summary with { BuildType = buildType };
            }
        }

        foreach (var pluginsDirectory in GetPluginDirectories())
        {
            if (!Directory.Exists(pluginsDirectory))
            {
                continue;
            }

            IEnumerable<string> pluginDirectories;
            try
            {
                pluginDirectories = Directory.EnumerateDirectories(pluginsDirectory, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var pluginDirectory in pluginDirectories)
            {
                var id = Path.GetFileName(pluginDirectory);
                if (string.IsNullOrWhiteSpace(id) || byId.ContainsKey(id))
                {
                    continue;
                }

                byId[id] = new PluginSummary(id, id, string.Empty, ResolvePluginBuildType(id));
            }
        }

        return byId.Values
            .OrderBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static PluginSummary? TryParseManifest(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);

            var root = document.RootElement;
            var id = GetStringProperty(root, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            var title = GetStringProperty(root, "name")
                        ?? GetStringProperty(root, "title")
                        ?? id;
            var version = GetStringProperty(root, "version") ?? string.Empty;

            double? thumbnailAspectRatio = null;
            string? thumbnailFit = null;
            int? thumbnailWidth = null;
            int? thumbnailHeight = null;
            string? searchExperienceJson = null;

            if (TryGetObjectProperty(root, "thumbnail", out var thumbnail))
            {
                thumbnailAspectRatio = GetDoubleProperty(thumbnail, "aspectRatio");
                thumbnailFit = GetStringProperty(thumbnail, "fit");
                thumbnailWidth = GetInt32Property(thumbnail, "width");
                thumbnailHeight = GetInt32Property(thumbnail, "height");

                if ((thumbnailAspectRatio is null || thumbnailAspectRatio <= 0)
                    && thumbnailWidth is { } width
                    && thumbnailHeight is { } height
                    && width > 0
                    && height > 0)
                {
                    thumbnailAspectRatio = (double)width / height;
                }
            }

            if (root.TryGetProperty("searchExperience", out var searchExperience)
                && searchExperience.ValueKind == JsonValueKind.Object)
            {
                searchExperienceJson = searchExperience.GetRawText();
            }

            return new PluginSummary(
                id,
                title,
                version,
                "csharp",
                thumbnailAspectRatio,
                thumbnailFit,
                thumbnailWidth,
                thumbnailHeight,
                searchExperienceJson);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolvePluginBuildType(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return "csharp";
        }

        foreach (var pluginsDirectory in GetPluginDirectories())
        {
            if (string.IsNullOrWhiteSpace(pluginsDirectory) || !Directory.Exists(pluginsDirectory))
            {
                continue;
            }

            var pluginRoot = Path.Combine(pluginsDirectory, pluginId);
            if (!Directory.Exists(pluginRoot))
            {
                continue;
            }

            if (ContainsComponentWasmArtifacts(pluginRoot))
            {
                return "cwasm";
            }

            if (ContainsCoreWasmArtifacts(pluginRoot))
            {
                return "wasm";
            }
        }

        return "csharp";
    }

    private static bool ContainsComponentWasmArtifacts(string pluginRoot)
    {
        static bool Exists(string path) => File.Exists(path);

        if (Exists(Path.Combine(pluginRoot, "plugin.cwasm"))
            || Exists(Path.Combine(pluginRoot, "wasm", "plugin.cwasm")))
        {
            return true;
        }

        try
        {
            return Directory.EnumerateFiles(pluginRoot, "*.cwasm", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsCoreWasmArtifacts(string pluginRoot)
    {
        static bool Exists(string path) => File.Exists(path);

        if (Exists(Path.Combine(pluginRoot, "plugin.wasm"))
            || Exists(Path.Combine(pluginRoot, "wasm", "plugin.wasm")))
        {
            return true;
        }

        try
        {
            return Directory.EnumerateFiles(pluginRoot, "*.wasm", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            var directValue = property.GetString();
            return string.IsNullOrWhiteSpace(directValue) ? null : directValue.Trim();
        }

        foreach (var candidate in element.EnumerateObject())
        {
            if (!string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                || candidate.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var matchedValue = candidate.Value.GetString();
            return string.IsNullOrWhiteSpace(matchedValue) ? null : matchedValue.Trim();
        }

        return null;
    }

    private static int? GetInt32Property(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var numeric))
        {
            return numeric;
        }

        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? GetDoubleProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var numeric))
        {
            return numeric;
        }

        if (property.ValueKind == JsonValueKind.String
            && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static IEnumerable<string> GetManifestDirectories()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddIfNotEmpty(set, _pluginPathConfiguration.ManifestsDirectory);

        AddIfNotEmpty(set, Environment.GetEnvironmentVariable("EMMA_MANIFESTS_DIR"));
        AddIfNotEmpty(set, Path.Combine(Environment.CurrentDirectory, "manifests"));
        AddIfNotEmpty(set, Path.Combine(AppContext.BaseDirectory, "manifests"));

        foreach (var supportDirectory in GetApplicationSupportDirectories())
        {
            AddIfNotEmpty(set, Path.Combine(supportDirectory, "manifests"));
            AddIfNotEmpty(set, Path.Combine(supportDirectory, "emmaui", "manifests"));
            AddIfNotEmpty(set, Path.Combine(supportDirectory, "com.example.emmaui", "emmaui", "manifests"));
        }

        return set;
    }

    private static IEnumerable<string> GetPluginDirectories()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddIfNotEmpty(set, _pluginPathConfiguration.PluginsDirectory);

        AddIfNotEmpty(set, Environment.GetEnvironmentVariable("EMMA_PLUGINS_DIR"));
        AddIfNotEmpty(set, Path.Combine(Environment.CurrentDirectory, "plugins"));
        AddIfNotEmpty(set, Path.Combine(AppContext.BaseDirectory, "plugins"));

        foreach (var supportDirectory in GetApplicationSupportDirectories())
        {
            AddIfNotEmpty(set, Path.Combine(supportDirectory, "plugins"));
            AddIfNotEmpty(set, Path.Combine(supportDirectory, "emmaui", "plugins"));
            AddIfNotEmpty(set, Path.Combine(supportDirectory, "com.example.emmaui", "emmaui", "plugins"));
        }

        return set;
    }

    private static IEnumerable<string> GetApplicationSupportDirectories()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddIfNotEmpty(set, Environment.GetEnvironmentVariable("EMMA_APP_SUPPORT_DIR"));

        if (OperatingSystem.IsLinux())
        {
            AddIfNotEmpty(set, Environment.GetEnvironmentVariable("XDG_DATA_HOME"));

            var homeEnv = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(homeEnv))
            {
                AddIfNotEmpty(set, Path.Combine(homeEnv, ".local", "share"));
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        if (!string.IsNullOrWhiteSpace(home))
        {
            AddIfNotEmpty(set, Path.Combine(home, "Library", "Application Support"));
        }

        return set;
    }

    private static void AddIfNotEmpty(ISet<string> set, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        set.Add(path);
    }

    private static string? NormalizeConfiguredPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private static string? NormalizeConfiguredValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static bool SameConfiguredPath(string? left, string? right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(left, right, comparison);
    }

    private static void AppendJsonProperty(StringBuilder sb, string name, string value)
    {
        AppendJsonString(sb, name);
        sb.Append(':');
        AppendJsonString(sb, value);
    }

    private static void AppendJsonNullableStringProperty(StringBuilder sb, string name, string? value)
    {
        AppendJsonString(sb, name);
        sb.Append(':');
        if (value is null)
        {
            sb.Append("null");
            return;
        }

        AppendJsonString(sb, value);
    }

    private static void AppendJsonBooleanProperty(StringBuilder sb, string name, bool value)
    {
        AppendJsonString(sb, name);
        sb.Append(':');
        sb.Append(value ? "true" : "false");
    }

    private static void AppendJsonNumberProperty(StringBuilder sb, string name, long value)
    {
        AppendJsonString(sb, name);
        sb.Append(':');
        sb.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendJsonDoubleProperty(StringBuilder sb, string name, double value)
    {
        AppendJsonString(sb, name);
        sb.Append(':');
        sb.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (char.IsControl(ch))
                    {
                        sb.Append("\\u");
                        sb.Append(((int)ch).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
