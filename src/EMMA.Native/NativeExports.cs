using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
    private sealed record HostRuntimeConfiguration(string? ExecutablePath, string? BaseUrl, string? Mode);

    private static readonly ConcurrentDictionary<int, RuntimeState> States = new();
    private static readonly HttpClient PluginHostClient = new();
    private static readonly Lock PluginHostSync = new();
    private static readonly string PluginHostModeInternal = "internal";
    private static readonly string PluginHostModeExternal = "external";
    private static readonly string PluginHostModeDisabled = "disabled";
    private static int _nextHandle;
    private static PluginPathConfiguration _pluginPathConfiguration = new(null, null);
    private static HostRuntimeConfiguration _hostRuntimeConfiguration = new(null, null, null);
    private static Process? _managedPluginHostProcess;
    private static string? _managedPluginHostStdoutLogPath;
    private static string? _managedPluginHostStderrLogPath;

    [ThreadStatic]
    private static string? _lastError;

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_start")]
    public static int RuntimeStart()
    {
        ClearLastError();

        try
        {
            EnsurePluginHostReady();

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

            if (States.IsEmpty)
            {
                StopManagedPluginHost();
            }
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_configure_host")]
    public static int RuntimeConfigureHost(IntPtr executablePathUtf8, IntPtr baseUrlUtf8, IntPtr modeUtf8)
    {
        ClearLastError();

        try
        {
            _hostRuntimeConfiguration = new HostRuntimeConfiguration(
                NormalizeConfiguredPath(PtrToString(executablePathUtf8)),
                NormalizeConfiguredValue(PtrToString(baseUrlUtf8)),
                NormalizeConfiguredValue(PtrToString(modeUtf8)));

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
        EnsurePluginHostReady();
        var baseUri = GetPluginHostBaseUri();

        var builder = new UriBuilder(new Uri(baseUri, "/pipeline/paged/search"));
        var escapedQuery = Uri.EscapeDataString(query ?? string.Empty);
        var escapedPluginId = Uri.EscapeDataString(pluginId);
        builder.Query = $"query={escapedQuery}&pluginId={escapedPluginId}";

        const int maxAttempts = 3;
        var attemptTimeout = TimeSpan.FromSeconds(25);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(attemptTimeout);
                response = await PluginHostClient.GetAsync(builder.Uri, timeoutCts.Token);
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                TryRecoverManagedPluginHost(TimeSpan.FromSeconds(2));
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken);
                continue;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken);
                continue;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                var hostState = DescribeManagedPluginHostState();
                throw new InvalidOperationException(
                    $"Plugin host request timed out at {baseUri} after {maxAttempts} attempts. {hostState}",
                    ex);
            }
            catch (HttpRequestException ex)
            {
                var hostState = DescribeManagedPluginHostState();
                throw new InvalidOperationException(
                    $"Plugin host was not reachable at {baseUri} after {maxAttempts} attempts. Transport detail: {ex.Message}. {hostState}",
                    ex);
            }

            using (response)
            {
            if (response.IsSuccessStatusCode)
            {
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

            if (attempt < maxAttempts
                && (response.StatusCode == HttpStatusCode.InternalServerError
                    || response.StatusCode == HttpStatusCode.BadGateway
                    || response.StatusCode == HttpStatusCode.ServiceUnavailable
                    || response.StatusCode == HttpStatusCode.GatewayTimeout))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken);
                continue;
            }

            var details = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(details))
            {
                throw new InvalidOperationException(details);
            }

            throw new InvalidOperationException(
                $"Plugin host search failed with status {(int)response.StatusCode}.");
            }
        }

        throw new InvalidOperationException("Plugin host search failed after retries.");
    }

    private static void EnsurePluginHostReady()
    {
        EnsurePluginHostReady(TimeSpan.FromSeconds(30));
    }

    private static void EnsurePluginHostReady(TimeSpan waitTimeout)
    {
        var mode = GetPluginHostMode();
        if (string.Equals(mode, PluginHostModeDisabled, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var baseUri = GetPluginHostBaseUri();

        if (string.Equals(mode, PluginHostModeExternal, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsPluginHostReachable(baseUri))
            {
                throw new InvalidOperationException(
                    $"PluginHost external mode requires a running host at {baseUri}.");
            }

            return;
        }

        if (!string.Equals(mode, PluginHostModeInternal, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Invalid EMMA_PLUGIN_HOST_MODE '{mode}'. Supported values: internal, external, disabled.");
        }

        if (IsPluginHostReachable(baseUri))
        {
            return;
        }

        lock (PluginHostSync)
        {
            if (IsPluginHostReachable(baseUri))
            {
                return;
            }

            if (_managedPluginHostProcess is not null && !_managedPluginHostProcess.HasExited)
            {
                if (WaitForPluginHost(baseUri, waitTimeout))
                {
                    return;
                }
            }

            _managedPluginHostProcess = StartManagedPluginHostProcess(baseUri);

            if (!WaitForPluginHost(baseUri, waitTimeout))
            {
                throw new InvalidOperationException(
                    $"PluginHost did not become reachable at {baseUri}.");
            }
        }
    }

    private static void StopManagedPluginHost()
    {
        lock (PluginHostSync)
        {
            if (_managedPluginHostProcess is null)
            {
                return;
            }

            try
            {
                if (!_managedPluginHostProcess.HasExited)
                {
                    _managedPluginHostProcess.Kill(entireProcessTree: true);
                    _managedPluginHostProcess.WaitForExit(3000);
                }
            }
            catch
            {
            }
            finally
            {
                _managedPluginHostProcess.Dispose();
                _managedPluginHostProcess = null;
            }
        }
    }

    private static Process StartManagedPluginHostProcess(Uri baseUri)
    {
        var startInfo = BuildPluginHostStartInfo(baseUri);
        var (stdoutLogPath, stderrLogPath) = CreateManagedPluginHostLogPaths();
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = false
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start PluginHost process.");
        }

        _managedPluginHostStdoutLogPath = stdoutLogPath;
        _managedPluginHostStderrLogPath = stderrLogPath;
        AttachManagedPluginHostLogCapture(process, stdoutLogPath, stderrLogPath);

        return process;
    }

    private static (string stdoutLogPath, string stderrLogPath) CreateManagedPluginHostLogPaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), "emma-pluginhost-logs");
        Directory.CreateDirectory(directory);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var nonce = Guid.NewGuid().ToString("N");
        return (
            Path.Combine(directory, $"pluginhost-{stamp}-{nonce}.stdout.log"),
            Path.Combine(directory, $"pluginhost-{stamp}-{nonce}.stderr.log"));
    }

    private static void AttachManagedPluginHostLogCapture(Process process, string stdoutLogPath, string stderrLogPath)
    {
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                TryAppendLogLine(stdoutLogPath, args.Data);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                TryAppendLogLine(stderrLogPath, args.Data);
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private static void TryAppendLogLine(string path, string line)
    {
        try
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static ProcessStartInfo BuildPluginHostStartInfo(Uri baseUri)
    {
        var workingDirectory = Environment.GetEnvironmentVariable("EMMA_PLUGIN_HOST_WORKING_DIR");
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            workingDirectory = AppContext.BaseDirectory;
        }

        var urlsValue = $"http://{baseUri.Host}:{baseUri.Port}";
        var manifestDirectory = ResolveManifestDirectory();
        var sandboxDirectory = ResolvePluginSandboxDirectory();

        var explicitExecutable = _hostRuntimeConfiguration.ExecutablePath
            ?? Environment.GetEnvironmentVariable("EMMA_PLUGIN_HOST_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(explicitExecutable))
        {
            if (!Path.IsPathRooted(explicitExecutable))
            {
                throw new InvalidOperationException(
                    "EMMA_PLUGIN_HOST_EXECUTABLE must be an absolute path in internal mode.");
            }

            if (!File.Exists(explicitExecutable))
            {
                throw new InvalidOperationException(
                    $"PluginHost executable not found: {explicitExecutable}");
            }

            if (explicitExecutable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = Quote(explicitExecutable),
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                };

                ApplyPluginHostEnvironment(startInfo, urlsValue, manifestDirectory, sandboxDirectory);
                return startInfo;
            }

            var execInfo = new ProcessStartInfo
            {
                FileName = explicitExecutable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            ApplyPluginHostEnvironment(execInfo, urlsValue, manifestDirectory, sandboxDirectory);
            return execInfo;
        }

        throw new InvalidOperationException(
            "PluginHost internal mode requires EMMA_PLUGIN_HOST_EXECUTABLE to point to a published PluginHost binary.");
    }

    private static void ApplyPluginHostEnvironment(
        ProcessStartInfo startInfo,
        string urlsValue,
        string? manifestDirectory,
        string? sandboxDirectory)
    {
        startInfo.Environment["ASPNETCORE_URLS"] = urlsValue;

        if (!string.IsNullOrWhiteSpace(manifestDirectory))
        {
            startInfo.Environment["PluginHost__ManifestDirectory"] = manifestDirectory;
        }

        if (!string.IsNullOrWhiteSpace(sandboxDirectory))
        {
            startInfo.Environment["PluginHost__SandboxRootDirectory"] = sandboxDirectory;
        }

        startInfo.Environment["PluginHost__HandshakeOnStartup"] = "false";
    }

    private static bool WaitForPluginHost(Uri baseUri, TimeSpan timeout)
    {
        var startedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startedAt < timeout)
        {
            if (IsPluginHostReachable(baseUri))
            {
                return true;
            }

            Thread.Sleep(200);
        }

        return false;
    }

    private static bool IsPluginHostReachable(Uri baseUri)
    {
        try
        {
            using var tcp = new TcpClient();
            var connectTask = tcp.ConnectAsync(baseUri.Host, baseUri.Port);
            var completed = connectTask.Wait(TimeSpan.FromMilliseconds(300));
            return completed && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static void TryRecoverManagedPluginHost(TimeSpan waitTimeout)
    {
        if (!string.Equals(GetPluginHostMode(), PluginHostModeInternal, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var baseUri = GetPluginHostBaseUri();

        lock (PluginHostSync)
        {
            if (_managedPluginHostProcess is not null
                && !_managedPluginHostProcess.HasExited
                && !IsPluginHostReachable(baseUri))
            {
                try
                {
                    _managedPluginHostProcess.Kill(entireProcessTree: true);
                    _managedPluginHostProcess.WaitForExit(1000);
                }
                catch
                {
                }
                finally
                {
                    _managedPluginHostProcess.Dispose();
                    _managedPluginHostProcess = null;
                }
            }

            if (_managedPluginHostProcess is not null && !_managedPluginHostProcess.HasExited)
            {
                return;
            }
        }

        try
        {
            EnsurePluginHostReady(waitTimeout);
        }
        catch
        {
        }
    }

    private static string DescribeManagedPluginHostState()
    {
        if (!string.Equals(GetPluginHostMode(), PluginHostModeInternal, StringComparison.OrdinalIgnoreCase))
        {
            return "PluginHost mode is not internal.";
        }

        lock (PluginHostSync)
        {
            if (_managedPluginHostProcess is null)
            {
                return $"No managed PluginHost process is tracked. {DescribeManagedPluginHostLogs()}";
            }

            if (_managedPluginHostProcess.HasExited)
            {
                return $"Managed PluginHost process exited with code {_managedPluginHostProcess.ExitCode}. {DescribeManagedPluginHostLogs()}";
            }

            return $"Managed PluginHost process is running (PID {_managedPluginHostProcess.Id}). {DescribeManagedPluginHostLogs()}";
        }
    }

    private static string DescribeManagedPluginHostLogs()
    {
        if (string.IsNullOrWhiteSpace(_managedPluginHostStdoutLogPath)
            && string.IsNullOrWhiteSpace(_managedPluginHostStderrLogPath))
        {
            return "No managed PluginHost log paths are set.";
        }

        var stderrPath = _managedPluginHostStderrLogPath;
        var stdoutPath = _managedPluginHostStdoutLogPath;
        var stderrTail = ReadLogTail(stderrPath, 20);
        var stdoutTail = ReadLogTail(stdoutPath, 8);

        return $"stdout={stdoutPath ?? "<null>"}; stderr={stderrPath ?? "<null>"}; stderrTail='{stderrTail}'; stdoutTail='{stdoutTail}'.";
    }

    private static string ReadLogTail(string? path, int maxLines)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return string.Empty;
            }

            var lines = File.ReadAllLines(path);
            if (lines.Length == 0)
            {
                return string.Empty;
            }

            var tail = lines.TakeLast(Math.Max(1, maxLines));
            return string.Join(" | ", tail).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Uri GetPluginHostBaseUri()
    {
        var baseUrl = _hostRuntimeConfiguration.BaseUrl
            ?? Environment.GetEnvironmentVariable("EMMA_PLUGIN_HOST_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "http://127.0.0.1:5223";
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("Invalid EMMA_PLUGIN_HOST_BASE_URL value.");
        }

        return baseUri;
    }

    private static string GetPluginHostMode()
    {
        var configured = _hostRuntimeConfiguration.Mode
            ?? Environment.GetEnvironmentVariable("EMMA_PLUGIN_HOST_MODE");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return PluginHostModeInternal;
        }

        return configured.Trim().ToLowerInvariant();
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

    private static string Quote(string value)
    {
        return '"' + value.Replace("\"", "\\\"") + '"';
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
