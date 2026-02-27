using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using EMMA.Api;
using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;

namespace EMMA.Native;

public static class NativeExports
{
    private sealed class RuntimeState(EmbeddedRuntime runtime, InMemoryMediaStore store)
    {
        public EmbeddedRuntime Runtime { get; } = runtime;
        public InMemoryMediaStore Store { get; } = store;
        public string? SelectedPluginId { get; set; }
    }
    private sealed record PluginSummary(string Id, string Title);
    private sealed record PluginPathConfiguration(string? ManifestsDirectory, string? PluginsDirectory);

    private static readonly ConcurrentDictionary<int, RuntimeState> States = new();
    private static readonly HttpClient PluginHostClient = new();
    private static int _nextHandle;
    private static PluginPathConfiguration _pluginPathConfiguration = new(null, null);

    [ThreadStatic]
    private static string? _lastError;

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_start")]
    public static int RuntimeStart()
    {
        ClearLastError();

        try
        {
            var store = new InMemoryMediaStore();
            IMediaSearchPort search = new InMemorySearchPort(store);
            IPageProviderPort pages = new InMemoryPageProvider(store);
            IPolicyEvaluator policy = new HostPolicyEvaluator();

            var runtime = EmbeddedRuntimeFactory.Create(search, pages, policy);

            var handle = Interlocked.Increment(ref _nextHandle);
            States[handle] = new RuntimeState(runtime, store);
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

        try
        {
            States.TryRemove(handle, out _);
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

            _pluginPathConfiguration = new PluginPathConfiguration(
                NormalizeConfiguredPath(manifestsDirectory),
                NormalizeConfiguredPath(pluginsDirectory));

            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
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

            IReadOnlyList<MediaSummary> results;
            if (!string.IsNullOrWhiteSpace(activePluginId))
            {
                results = SearchViaPluginHost(activePluginId, query, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                results = state.Runtime.Pipeline
                    .SearchAsync(query, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }

            var json = BuildMediaJson(results);
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
        if (string.IsNullOrWhiteSpace(_lastError))
        {
            return IntPtr.Zero;
        }

        return AllocUtf8(_lastError);
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

    private static void ClearLastError()
    {
        _lastError = null;
    }

    private static void SetLastError(string message)
    {
        _lastError = message;
    }

    private static void SetLastError(Exception ex)
    {
        _lastError = ex.Message;
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

    private static async Task<IReadOnlyList<MediaSummary>> SearchViaPluginHost(
        string pluginId,
        string query,
        CancellationToken cancellationToken)
    {
        var baseUrl = Environment.GetEnvironmentVariable("EMMA_PLUGIN_HOST_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "http://localhost:5223";
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("Invalid EMMA_PLUGIN_HOST_BASE_URL value.");
        }

        var builder = new UriBuilder(new Uri(baseUri, "/pipeline/paged/search"));
        var escapedQuery = Uri.EscapeDataString(query ?? string.Empty);
        var escapedPluginId = Uri.EscapeDataString(pluginId);
        builder.Query = $"query={escapedQuery}&pluginId={escapedPluginId}";

        using var response = await PluginHostClient.GetAsync(builder.Uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(details))
            {
                throw new InvalidOperationException(details);
            }

            throw new InvalidOperationException(
                $"Plugin host search failed with status {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<MediaSummary>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = GetStringProperty(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var source = GetStringProperty(item, "source") ?? string.Empty;
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

                byId[summary.Id] = summary;
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

                byId[id] = new PluginSummary(id, id);
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

            return new PluginSummary(id, title);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

    private static void AppendJsonProperty(StringBuilder sb, string name, string value)
    {
        AppendJsonString(sb, name);
        sb.Append(':');
        AppendJsonString(sb, value);
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
