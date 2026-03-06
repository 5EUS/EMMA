using System.Collections.Concurrent;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Globalization;
using EMMA.Api;
using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;
using EMMA.PluginHost.Library;

namespace EMMA.Native;

public static class NativeExports
{
    private sealed class RuntimeState(EmbeddedRuntime runtime, InMemoryMediaStore store)
    {
        public EmbeddedRuntime Runtime { get; } = runtime;
        public InMemoryMediaStore Store { get; } = store;
        public string? SelectedPluginId { get; set; }
    }
    private sealed record PluginSummary(string Id, string Title, string BuildType);
    private sealed record PluginPathConfiguration(string? ManifestsDirectory, string? PluginsDirectory);

    private static readonly ConcurrentDictionary<int, RuntimeState> States = new();
    private static readonly Lock PluginHostInitLock = new();
    private static readonly Lock ErrorLock = new();
    private static readonly NativeLogStore LogStore = new();
    private static int _nextHandle;
    private static PluginPathConfiguration _pluginPathConfiguration = new(null, null);
    private static bool _pluginHostInitialized = false;

    // Don't use [ThreadStatic] - we need the error to be visible across threads for FFI
    private static string? _lastError;

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_start")]
    public static int RuntimeStart()
    {
        ClearLastError();
        LogInfo("runtime", "RuntimeStart requested.");

        try
        {
            EnsurePluginHostInitialized();

            var store = new InMemoryMediaStore();
            IMediaSearchPort search = new InMemorySearchPort(store);
            IPageProviderPort pages = new InMemoryPageProvider(store);
            IPolicyEvaluator policy = new HostPolicyEvaluator();

            var runtime = EmbeddedRuntimeFactory.Create(search, pages, policy);

            var handle = Interlocked.Increment(ref _nextHandle);
            States[handle] = new RuntimeState(runtime, store);
            LogInfo("runtime", $"Runtime started. handle={handle}");
            return handle;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_stop")]
    public static void RuntimeStop(int handle)
    {
        ClearLastError();
        LogInfo("runtime", $"RuntimeStop requested. handle={handle}");

        try
        {
            States.TryRemove(handle, out _);

            if (States.IsEmpty)
            {
                // Optionally shutdown plugin host when all runtimes are stopped
                ShutdownPluginHost();
            }

            LogInfo("runtime", $"Runtime stopped. handle={handle}");
        }
        catch (Exception ex)
        {
            SetLastError(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_status")]
    public static int RuntimeStatus(int handle)
    {
        return States.ContainsKey(handle) ? 1 : 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_list_media_json")]
    public static IntPtr RuntimeListMediaJson(int handle)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out var state))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            var results = state.Runtime.Pipeline
                .SearchAsync(string.Empty, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var json = BuildMediaJson(results);
            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_list_plugins_json")]
    public static IntPtr RuntimeListPluginsJson()
    {
        ClearLastError();

        try
        {
            var plugins = DiscoverPlugins();
            return AllocUtf8(BuildPluginsJson(plugins));
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_configure_paths")]
    public static int RuntimeConfigurePaths(IntPtr manifestsDirUtf8, IntPtr pluginsDirUtf8)
    {
        ClearLastError();

        try
        {
            var manifestsDirectory = PtrToString(manifestsDirUtf8);
            var pluginsDirectory = PtrToString(pluginsDirUtf8);

            var previousConfiguration = _pluginPathConfiguration;

            _pluginPathConfiguration = new PluginPathConfiguration(
                NormalizeConfiguredPath(manifestsDirectory),
                NormalizeConfiguredPath(pluginsDirectory));

            if (_pluginHostInitialized)
            {
                lock (PluginHostInitLock)
                {
                    if (_pluginHostInitialized)
                    {
                        var pathsChanged = !SameConfiguredPath(previousConfiguration.ManifestsDirectory, _pluginPathConfiguration.ManifestsDirectory)
                            || !SameConfiguredPath(previousConfiguration.PluginsDirectory, _pluginPathConfiguration.PluginsDirectory);

                        if (pathsChanged)
                        {
                            ShutdownPluginHost();
                            EnsurePluginHostInitialized();
                            LogInfo("plugin-host", "Plugin host reconfigured with updated manifests/plugins paths.");
                        }
                        else
                        {
                            var rescanCode = PluginHostExports.RescanManaged();
                            if (rescanCode != 0)
                            {
                                var error = PluginHostExports.GetLastErrorManaged()
                                    ?? $"Plugin host rescan failed with code {rescanCode}";
                                throw new InvalidOperationException(error);
                            }

                            LogInfo("plugin-host", "Plugin host rescanned with existing manifests/plugins paths.");
                        }
                    }
                }
            }

            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_configure_host")]
    public static int RuntimeConfigureHost(IntPtr executablePathUtf8, IntPtr baseUrlUtf8, IntPtr modeUtf8)
    {
        ClearLastError();

        // This method is deprecated - plugin host is now embedded in-process
        // Configuration parameters are ignored
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_open_plugin")]
    public static int RuntimeOpenPlugin(int handle, IntPtr pluginIdUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out var state))
            {
                SetLastError("Runtime handle not found.");
                return 0;
            }

            var pluginId = PtrToString(pluginIdUtf8);
            if (string.IsNullOrWhiteSpace(pluginId))
            {
                SetLastError("pluginId is required.");
                return 0;
            }

            var plugins = DiscoverPlugins();
            if (!plugins.Any(plugin => string.Equals(plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase)))
            {
                SetLastError($"Plugin '{pluginId}' was not found in configured manifests/plugins directories.");
                return 0;
            }

            state.SelectedPluginId = pluginId.Trim();
            LogInfo("plugin", $"Opened plugin '{state.SelectedPluginId}' for handle={handle}");

            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_search_media_json")]
    public static IntPtr RuntimeSearchMediaJson(int handle, IntPtr queryUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out var state))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            var query = PtrToString(queryUtf8) ?? string.Empty;
            var activePluginId = ResolveActivePluginId(state);
            LogDebug("search", $"Search requested. handle={handle}, pluginId={activePluginId ?? "<none>"}, query='{query}'");

            IReadOnlyList<MediaSummary> results;
            if (!string.IsNullOrWhiteSpace(activePluginId))
            {
                results = SearchViaEmbeddedPluginHost(activePluginId, query);
            }
            else
            {
                results = state.Runtime.Pipeline
                    .SearchAsync(query, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }

            var json = BuildMediaJson(results);
            LogDebug("search", $"Search completed. handle={handle}, count={results.Count}");
            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_last_error")]
    public static IntPtr LastError()
    {
        lock (ErrorLock)
        {
            if (string.IsNullOrWhiteSpace(_lastError))
            {
                return IntPtr.Zero;
            }

            return AllocUtf8(_lastError);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_string_free")]
    public static void StringFree(IntPtr value)
    {
        if (value == IntPtr.Zero)
        {
            return;
        }

        Marshal.FreeCoTaskMem(value);
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_log_read_json")]
    public static IntPtr LogReadJson(long afterSequence, int maxItems)
    {
        try
        {
            var entries = LogStore.ReadSince(afterSequence, maxItems);
            return AllocUtf8(BuildLogsJson(entries));
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_log_latest_seq")]
    public static long LogLatestSequence()
    {
        return LogStore.LatestSequence;
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_log_set_console_enabled")]
    public static void LogSetConsoleEnabled(int enabled)
    {
        LogStore.SetConsoleEnabled(enabled != 0);
        LogInfo("logging", $"Console logging {(enabled != 0 ? "enabled" : "disabled")}");
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_log_clear")]
    public static void LogClear()
    {
        LogStore.Clear();
        LogInfo("logging", "Log store cleared.");
    }

    private static void ClearLastError()
    {
        lock (ErrorLock)
        {
            _lastError = null;
        }
    }

    private static void SetLastError(string message)
    {
        lock (ErrorLock)
        {
            _lastError = message;
        }

        LogError("error", message);
    }

    private static void SetLastError(Exception ex)
    {
        lock (ErrorLock)
        {
            _lastError = $"{ex.GetType().Name}: {ex.Message}";
        }

        LogError("exception", $"{ex.GetType().Name}: {ex.Message}");
    }

    private static IntPtr AllocUtf8(string value)
    {
        return Marshal.StringToCoTaskMemUTF8(value);
    }

    private static string? PtrToString(IntPtr value)
    {
        return value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);
    }

    private static string BuildMediaJson(IReadOnlyList<MediaSummary> results)
    {
        var sb = new StringBuilder();
        sb.Append('[');

        for (var i = 0; i < results.Count; i++)
        {
            var item = results[i];
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('{');
            AppendJsonProperty(sb, "id", item.Id.ToString());
            sb.Append(',');
            AppendJsonProperty(sb, "source", item.SourceId);
            sb.Append(',');
            AppendJsonProperty(sb, "title", item.Title);
            sb.Append(',');
            AppendJsonProperty(sb, "mediaType", item.MediaType.ToString().ToLowerInvariant());
            sb.Append('}');
        }

        sb.Append(']');
        return sb.ToString();
    }

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

    private static IReadOnlyList<MediaSummary> SearchViaEmbeddedPluginHost(string pluginId, string query)
    {
        EnsurePluginHostInitialized();

        var json = PluginHostExports.SearchJsonManaged(pluginId, query);
        if (json == null)
        {
            var error = PluginHostExports.GetLastErrorManaged() ?? "Plugin host search returned null";
            throw new InvalidOperationException(error);
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<MediaSummary>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = GetMediaIdProperty(item);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var source = GetStringProperty(item, "source")
                ?? GetStringProperty(item, "sourceId")
                ?? string.Empty;
            var title = GetStringProperty(item, "title") ?? string.Empty;
            var mediaType = GetStringProperty(item, "mediaType");

            results.Add(new MediaSummary(
                MediaId.Create(id),
                source,
                title,
                string.Equals(mediaType, "video", StringComparison.OrdinalIgnoreCase)
                    ? MediaType.Video
                    : MediaType.Paged));
        }

        return results;
    }

    private static string? GetMediaIdProperty(JsonElement element)
    {
        var directId = GetStringProperty(element, "id")
            ?? GetStringProperty(element, "mediaId");
        if (!string.IsNullOrWhiteSpace(directId))
        {
            return directId;
        }

        if (TryGetObjectProperty(element, "id", out var idObject))
        {
            var nestedId = GetStringProperty(idObject, "value");
            if (!string.IsNullOrWhiteSpace(nestedId))
            {
                return nestedId;
            }
        }

        if (TryGetObjectProperty(element, "mediaId", out var mediaIdObject))
        {
            var nestedId = GetStringProperty(mediaIdObject, "value");
            if (!string.IsNullOrWhiteSpace(nestedId))
            {
                return nestedId;
            }
        }

        return null;
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

    private static void EnsurePluginHostInitialized()
    {
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

    private static string BuildLogsJson(IReadOnlyList<NativeLogEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append('[');

        for (var i = 0; i < entries.Count; i++)
        {
            var item = entries[i];
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('{');
            AppendJsonNumberProperty(sb, "seq", item.Sequence);
            sb.Append(',');
            AppendJsonProperty(sb, "ts", item.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
            sb.Append(',');
            AppendJsonProperty(sb, "level", item.Level.ToString());
            sb.Append(',');
            AppendJsonProperty(sb, "category", item.Category);
            sb.Append(',');
            AppendJsonProperty(sb, "message", item.Message);
            sb.Append('}');
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static void LogDebug(string category, string message)
    {
        LogStore.Write(NativeLogLevel.Debug, category, message);
    }

    private static void LogInfo(string category, string message)
    {
        LogStore.Write(NativeLogLevel.Information, category, message);
    }

    private static void LogError(string category, string message)
    {
        LogStore.Write(NativeLogLevel.Error, category, message);
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
            AppendJsonProperty(sb, "buildType", plugin.BuildType);
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

                byId[id] = new PluginSummary(id, id, ResolvePluginBuildType(id));
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

            return new PluginSummary(id, title, "csharp");
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

            if (ContainsWasmArtifacts(pluginRoot))
            {
                return "wasm";
            }
        }

        return "csharp";
    }

    private static bool ContainsWasmArtifacts(string pluginRoot)
    {
        static bool Exists(string path) => File.Exists(path);

        if (Exists(Path.Combine(pluginRoot, "plugin.wasm"))
            || Exists(Path.Combine(pluginRoot, "plugin.cwasm"))
            || Exists(Path.Combine(pluginRoot, "wasm", "plugin.wasm"))
            || Exists(Path.Combine(pluginRoot, "wasm", "plugin.cwasm")))
        {
            return true;
        }

        try
        {
            return Directory.EnumerateFiles(pluginRoot, "*.wasm", SearchOption.AllDirectories).Any()
                || Directory.EnumerateFiles(pluginRoot, "*.cwasm", SearchOption.AllDirectories).Any();
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

    private static void AppendJsonNumberProperty(StringBuilder sb, string name, long value)
    {
        AppendJsonString(sb, name);
        sb.Append(':');
        sb.Append(value.ToString(CultureInfo.InvariantCulture));
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
