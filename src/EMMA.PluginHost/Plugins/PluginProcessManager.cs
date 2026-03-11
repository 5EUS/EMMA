using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Platform;
using EMMA.PluginHost.Sandboxing;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Minimal process supervisor for plugin startup and shutdown.
/// TODO: Deprecate once a dedicated supervisor service is implemented.
/// </summary>
public sealed class PluginProcessManager(
    IOptions<PluginHostOptions> options,
    IPluginSandboxManager sandboxManager,
    IPluginEntrypointResolver entrypointResolver,
    IOptions<PluginSignatureOptions> signatureOptions,
    IPluginSignatureVerifier signatureVerifier,
    ILogger<PluginProcessManager> logger)
{
    private const string HostAuthTokenEnvVar = "EMMA_PLUGIN_HOST_AUTH_TOKEN";
    private static readonly Regex CorrelationIdRegex = new(
        "CorrelationId\\s*[:=]\\s*(?<id>[A-Za-z0-9-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private sealed class ProcessHandle(Process process, string startupCommand, DateTimeOffset startedAt)
    {
        public Process Process { get; } = process;
        public string StartupCommand { get; } = startupCommand;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public DateTimeOffset LastUsedAt { get; set; } = startedAt;
        public int ActiveLeases { get; set; }
    }

    private sealed class UsageLease(PluginProcessManager owner, string pluginId) : IDisposable
    {
        private readonly PluginProcessManager _owner = owner;
        private readonly string _pluginId = pluginId;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _owner.ReleaseUsageLease(_pluginId);
        }
    }

    private sealed class NoOpLease : IDisposable
    {
        public static readonly NoOpLease Instance = new();
        public void Dispose() { }
    }
    private sealed class LogBuffer(int maxLines)
    {
        private readonly int _maxLines = Math.Max(10, maxLines);
        private readonly Queue<string> _lines = new();
        private readonly Lock _lock = new();

        public void Append(string line)
        {
            lock (_lock)
            {
                _lines.Enqueue(line);
                while (_lines.Count > _maxLines)
                {
                    _lines.Dequeue();
                }
            }
        }

        public IReadOnlyList<string> Snapshot(int? take)
        {
            lock (_lock)
            {
                if (_lines.Count == 0)
                {
                    return [];
                }

                var list = _lines.ToList();
                if (take is null || take <= 0 || take >= list.Count)
                {
                    return list;
                }

                return list.Skip(Math.Max(0, list.Count - take.Value)).ToList();
            }
        }
    }

    private readonly PluginHostOptions _options = options.Value;
    private readonly PluginSignatureOptions _signatureOptions = signatureOptions.Value;
    private readonly IPluginSignatureVerifier _signatureVerifier = signatureVerifier;
    private readonly IPluginSandboxManager _sandboxManager = sandboxManager;
    private readonly IPluginEntrypointResolver _entrypointResolver = entrypointResolver;
    private readonly ILogger<PluginProcessManager> _logger = logger;
    private readonly Dictionary<string, ProcessHandle> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _hostAuthTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LogBuffer> _logs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SemaphoreSlim> _startupGuards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public async Task<PluginRuntimeStatus> EnsureStartedAsync(
        PluginManifest manifest,
        PluginRuntimeStatus current,
        CancellationToken cancellationToken)
    {
        AppendProcessEvent(
            manifest.Id,
            $"ensure-start requested state={current.State} protocol={manifest.Protocol ?? "<none>"} endpoint={manifest.Endpoint ?? "<none>"}");

        var guardKey = string.IsNullOrWhiteSpace(manifest.Id) ? "<unknown>" : manifest.Id;
        var startupGuard = GetStartupGuard(guardKey);
        await startupGuard.WaitAsync(cancellationToken);

        try
        {
            if (string.IsNullOrWhiteSpace(manifest.Protocol))
            {
                AppendProcessEvent(manifest.Id, "startup aborted: protocol missing");
                return current.WithState(PluginRuntimeState.Disabled, "protocol-missing", "Plugin manifest protocol is missing.");
            }


            if (current.State == PluginRuntimeState.Timeout
                && current.NextRetryAt.HasValue
                && current.NextRetryAt.Value > DateTimeOffset.UtcNow)
            {
                return current;
            }

            if (current.State == PluginRuntimeState.Quarantined)
            {
                return current;
            }

            if (!TryValidateMinHostVersion(manifest, out var hostVersionReason))
            {
                return current.WithState(
                    PluginRuntimeState.Disabled,
                    "host-version-incompatible",
                    hostVersionReason ?? "Plugin runtime minHostVersion is incompatible with this host.");
            }

            var hasEndpoint = !string.IsNullOrWhiteSpace(manifest.Endpoint);
            var allowsProcess = HostPlatformPolicy.AllowsProcessPlugins(_options);
            var allowsWasm = HostPlatformPolicy.AllowsWasmPlugins(_options);
            var allowsExternal = HostPlatformPolicy.AllowsExternalEndpointPlugins(_options);

            if (_entrypointResolver.TryResolveWasmComponent(manifest, out _))
            {
                if (!allowsWasm)
                {
                    AppendProcessEvent(manifest.Id, "startup blocked: wasm component detected but wasm runtime is disabled by strategy");
                    return current.WithState(
                        PluginRuntimeState.Disabled,
                        "wasm-plugins-disabled",
                        "WASM component plugins are disabled by runtime strategy.");
                }

                AppendProcessEvent(manifest.Id, "startup skipped: wasm component plugin (external runtime)");
                return PluginRuntimeStatus.External();
            }

            if (!allowsProcess)
            {
                if (hasEndpoint && allowsExternal)
                {
                    AppendProcessEvent(manifest.Id, "startup skipped: process runtime disabled by strategy; using external endpoint runtime");
                    return PluginRuntimeStatus.External();
                }

                AppendProcessEvent(
                    manifest.Id,
                    "startup blocked: process runtime disabled by strategy and no eligible external endpoint runtime path");
                return current.WithState(
                    PluginRuntimeState.Disabled,
                    "process-plugins-unsupported",
                    "Process-managed plugins are disabled by runtime strategy.");
            }

            if (_signatureOptions.RequireSignedPlugins)
            {
                if (!_signatureVerifier.Verify(manifest, out var reason))
                {
                    return current.WithState(
                        PluginRuntimeState.Disabled,
                        "signature-invalid",
                        reason ?? "Plugin signature validation failed.");
                }
            }

            if (TryGetProcess(manifest.Id, out var existing))
            {
                if (!existing.Process.HasExited)
                {
                    AppendProcessEvent(manifest.Id, $"startup skipped: process already running pid={existing.Process.Id}");
                    return current.WithState(PluginRuntimeState.Running, current.LastErrorCode, current.LastErrorMessage);
                }

                AppendProcessEvent(manifest.Id, $"existing process was exited pid={existing.Process.Id}, removing stale handle");
                RemoveProcess(manifest.Id);
            }

            var executable = (string?)null;
            if (hasEndpoint && !TryResolveEntrypoint(manifest, out executable))
            {
                AppendProcessEvent(manifest.Id, "startup skipped: endpoint configured but no local entrypoint resolvable");
                if (allowsExternal)
                {
                    return current.State == PluginRuntimeState.Unknown
                        ? PluginRuntimeStatus.External()
                        : current;
                }

                return current.WithState(
                    PluginRuntimeState.Disabled,
                    "external-runtime-disabled",
                    "Endpoint is configured but external endpoint runtime is disabled by runtime strategy.");
            }

            AppendProcessEvent(manifest.Id, "preparing sandbox");
            await _sandboxManager.PrepareAsync(manifest, cancellationToken);
            AppendProcessEvent(manifest.Id, "sandbox prepared");

            var hostAuthToken = GenerateHostAuthToken();
            var startInfo = BuildStartInfo(manifest, executable);
            ApplyPluginPort(manifest, startInfo);
            ApplyHostAuthToken(startInfo, hostAuthToken);
            startInfo = _sandboxManager.ApplyToStartInfo(manifest, startInfo);
            AppendProcessEvent(
                manifest.Id,
                $"launch envelope file={startInfo.FileName} cwd={startInfo.WorkingDirectory} args={startInfo.Arguments} fileExists={File.Exists(startInfo.FileName ?? string.Empty)} cwdExists={Directory.Exists(startInfo.WorkingDirectory ?? string.Empty)} envPort={ReadEnv(startInfo, "EMMA_PLUGIN_PORT") ?? "<none>"}");
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Starting plugin {PluginId} with {FileName} {Arguments} (cwd={WorkingDirectory})",
                    manifest.Id,
                    startInfo.FileName,
                    startInfo.Arguments,
                    string.IsNullOrWhiteSpace(startInfo.WorkingDirectory) ? "<none>" : startInfo.WorkingDirectory);
            }
            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            if (OperatingSystem.IsIOS())
            {
                AppendProcessEvent(manifest.Id, "startup blocked: process runtime is unsupported on iOS");
                return current.WithState(
                    PluginRuntimeState.Disabled,
                    "process-runtime-ios-unsupported",
                    "Process-managed plugins are unsupported on iOS.");
            }

            if (!process.Start())
            {
                AppendProcessEvent(manifest.Id, "process.Start() returned false");
                return current.WithState(PluginRuntimeState.Crashed, "start-failed", "Failed to start plugin process.");
            }

            AppendProcessEvent(manifest.Id, $"process started pid={process.Id}");

            AddProcess(manifest.Id, process, startInfo.FileName ?? string.Empty, hostAuthToken);
            AttachLogCapture(manifest.Id, process);

            if (process.HasExited)
            {
                var exitCode = SafeExitCode(process);
                var details = BuildProcessExitDetails(manifest.Id, process);
                AppendProcessEvent(manifest.Id, $"process exited immediately exitCode={exitCode?.ToString() ?? "unknown"}");
                RemoveProcess(manifest.Id);
                return current.WithState(
                    PluginRuntimeState.Crashed,
                    "process-exited",
                    details,
                    exitCode);
            }

            await _sandboxManager.EnforceAsync(manifest, process, cancellationToken);

            if (string.IsNullOrWhiteSpace(manifest.Endpoint)
                || !Uri.TryCreate(manifest.Endpoint, UriKind.Absolute, out var address))
            {
                AppendProcessEvent(manifest.Id, "startup ready: no endpoint probe required");
                return PluginRuntimeStatus.Running();
            }

            AppendProcessEvent(manifest.Id, $"probing endpoint {address}");
            var ready = await WaitForEndpointAsync(
                address,
                TimeSpan.FromSeconds(_options.StartupTimeoutSeconds),
                TimeSpan.FromMilliseconds(_options.StartupProbeIntervalMs),
                cancellationToken);

            if (!ready)
            {
                if (process.HasExited)
                {
                    var exitCode = SafeExitCode(process);
                    var details = BuildProcessExitDetails(manifest.Id, process);
                    AppendProcessEvent(manifest.Id, $"process exited during startup exitCode={exitCode?.ToString() ?? "unknown"}");
                    RemoveProcess(manifest.Id);
                    return current.WithState(
                        PluginRuntimeState.Crashed,
                        "process-exited",
                        details,
                        exitCode);
                }
                await StopAsync(manifest.Id, cancellationToken);
                AppendProcessEvent(manifest.Id, $"startup timeout after {_options.StartupTimeoutSeconds}s");
                return current.WithRetry(
                    current.RetryCount + 1,
                    DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, _options.TimeoutBackoffSeconds) * (current.RetryCount + 1)),
                    "startup-timeout",
                    "Plugin did not become ready in time.");
            }

            AppendProcessEvent(manifest.Id, "startup ready: endpoint probe succeeded");
            return PluginRuntimeStatus.Running();
        }
        finally
        {
            startupGuard.Release();
        }
    }

    private SemaphoreSlim GetStartupGuard(string pluginId)
    {
        lock (_lock)
        {
            if (_startupGuards.TryGetValue(pluginId, out var existing))
            {
                return existing;
            }

            var created = new SemaphoreSlim(1, 1);
            _startupGuards[pluginId] = created;
            return created;
        }
    }

    private bool TryValidateMinHostVersion(PluginManifest manifest, out string? reason)
    {
        reason = null;

        var minHostVersion = manifest.Runtime?.MinHostVersion;
        if (string.IsNullOrWhiteSpace(minHostVersion))
        {
            return true;
        }

        if (!TryParseVersion(minHostVersion, out var required))
        {
            reason = $"Invalid runtime.minHostVersion '{minHostVersion}'.";
            return false;
        }

        var hostVersionText = typeof(PluginProcessManager).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        if (!TryParseVersion(hostVersionText, out var current))
        {
            reason = $"Host version '{hostVersionText}' is not parseable for compatibility checks.";
            return false;
        }

        if (current < required)
        {
            reason = $"Plugin requires host version >= {required}, current host is {current}.";
            return false;
        }

        return true;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var normalized = value.Trim();
        var prereleaseIndex = normalized.IndexOf('-');
        if (prereleaseIndex >= 0)
        {
            normalized = normalized[..prereleaseIndex];
        }

        return Version.TryParse(normalized, out version!);
    }

    public async Task StopAsync(string pluginId, CancellationToken cancellationToken)
    {
        ProcessHandle? handle = null;
        lock (_lock)
        {
            if (_processes.TryGetValue(pluginId, out var existing))
            {
                handle = existing;
                _processes.Remove(pluginId);
            }
        }

        if (handle is null)
        {
            return;
        }

        await StopHandleAsync(pluginId, handle, cancellationToken);
    }

    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        List<string> pluginIds;
        lock (_lock)
        {
            pluginIds = _processes.Keys.ToList();
        }

        foreach (var pluginId in pluginIds)
        {
            await StopAsync(pluginId, cancellationToken);
        }
    }

    public IDisposable AcquireUsageLease(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return NoOpLease.Instance;
        }

        lock (_lock)
        {
            if (!_processes.TryGetValue(pluginId, out var handle) || handle.Process.HasExited)
            {
                return NoOpLease.Instance;
            }

            handle.ActiveLeases++;
            handle.LastUsedAt = DateTimeOffset.UtcNow;
            return new UsageLease(this, pluginId);
        }
    }

    public async Task<IReadOnlyList<string>> StopIdleProcessesAsync(CancellationToken cancellationToken)
    {
        var idleFor = TimeSpan.FromSeconds(Math.Max(1, _options.PluginIdleTimeoutSeconds));
        var now = DateTimeOffset.UtcNow;

        List<(string PluginId, ProcessHandle Handle)> toStop = [];
        lock (_lock)
        {
            foreach (var entry in _processes)
            {
                var handle = entry.Value;

                if (handle.ActiveLeases > 0)
                {
                    continue;
                }

                if (handle.Process.HasExited || now - handle.LastUsedAt >= idleFor)
                {
                    toStop.Add((entry.Key, handle));
                }
            }

            foreach (var entry in toStop)
            {
                _processes.Remove(entry.PluginId);
            }
        }

        if (toStop.Count == 0)
        {
            return [];
        }

        var stopped = new List<string>(toStop.Count);
        foreach (var entry in toStop)
        {
            await StopHandleAsync(entry.PluginId, entry.Handle, cancellationToken);
            stopped.Add(entry.PluginId);
        }

        return stopped;
    }

    public PluginRuntimeStatus RecordTimeout(PluginRuntimeStatus current)
    {
        var retryCount = current.RetryCount + 1;
        if (_options.MaxTimeoutRetries > 0 && retryCount > _options.MaxTimeoutRetries)
        {
            return current.Quarantined("timeout-quarantine", "Plugin exceeded timeout retry limit.");
        }

        var nextRetryAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, _options.TimeoutBackoffSeconds) * retryCount);
        return current.WithRetry(retryCount, nextRetryAt, "rpc-timeout", "Plugin RPC timed out.");
    }

    public bool IsProcessRunning(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return false;
        }

        if (!TryGetProcess(pluginId, out var handle))
        {
            return false;
        }

        return !handle.Process.HasExited;
    }

    private bool TryGetProcess(string pluginId, out ProcessHandle handle)
    {
        lock (_lock)
        {
            return _processes.TryGetValue(pluginId, out handle!);
        }
    }

    private void AddProcess(string pluginId, Process process, string command)
    {
        lock (_lock)
        {
            _processes[pluginId] = new ProcessHandle(process, command, DateTimeOffset.UtcNow);
            _logs.TryAdd(pluginId, new LogBuffer(_options.PluginLogMaxLines));
        }
    }

    private void AddProcess(string pluginId, Process process, string command, string hostAuthToken)
    {
        lock (_lock)
        {
            _processes[pluginId] = new ProcessHandle(process, command, DateTimeOffset.UtcNow);
            _hostAuthTokens[pluginId] = hostAuthToken;
            _logs.TryAdd(pluginId, new LogBuffer(_options.PluginLogMaxLines));
        }
    }

    private void AppendProcessEvent(string pluginId, string message)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        LogBuffer buffer;
        lock (_lock)
        {
            if (!_logs.TryGetValue(pluginId, out buffer!))
            {
                buffer = new LogBuffer(_options.PluginLogMaxLines);
                _logs[pluginId] = buffer;
            }
        }

        var line = $"[startup] {message}";
        buffer.Append(line);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Plugin startup {PluginId} {Message}", pluginId, message);
        }
    }

    private void ReleaseUsageLease(string pluginId)
    {
        lock (_lock)
        {
            if (!_processes.TryGetValue(pluginId, out var handle))
            {
                return;
            }

            if (handle.ActiveLeases > 0)
            {
                handle.ActiveLeases--;
            }

            handle.LastUsedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task StopHandleAsync(string pluginId, ProcessHandle handle, CancellationToken cancellationToken)
    {
        try
        {
            if (!handle.Process.HasExited)
            {
                if (OperatingSystem.IsIOS())
                {
                    AppendProcessEvent(pluginId, "stop skipped: process kill unsupported on iOS");
                    return;
                }

                handle.Process.Kill(entireProcessTree: true);
            }

            await handle.Process.WaitForExitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Failed to stop plugin process {PluginId}.", pluginId);
            }
        }
    }

    private void RemoveProcess(string pluginId)
    {
        lock (_lock)
        {
            _processes.Remove(pluginId);
            _hostAuthTokens.Remove(pluginId);
        }
    }

    public string? GetHostAuthToken(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return null;
        }

        lock (_lock)
        {
            return _hostAuthTokens.TryGetValue(pluginId, out var token)
                ? token
                : null;
        }
    }

    public IReadOnlyList<string> GetLogs(string pluginId, int? take)
    {
        lock (_lock)
        {
            if (!_logs.TryGetValue(pluginId, out var buffer))
            {
                return [];
            }

            return buffer.Snapshot(take);
        }
    }

    private static int? SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    private string BuildProcessExitDetails(string pluginId, Process process)
    {
        var exitCode = SafeExitCode(process);
        var logs = GetLogs(pluginId, 20);
        var output = TryReadProcessOutput(process);
        var hint = BuildExitCodeHint(exitCode);

        var parts = new List<string>
        {
            $"Plugin process exited during startup (exitCode={exitCode?.ToString() ?? "unknown"})."
        };

        if (!string.IsNullOrWhiteSpace(hint))
        {
            parts.Add(hint!);
        }

        if (!string.IsNullOrWhiteSpace(output))
        {
            parts.Add($"Process output: {output}");
        }

        if (logs.Count > 0)
        {
            var tail = string.Join(" | ", logs.Select(line => line.Trim()));
            parts.Add($"Recent logs: {tail}");
        }

        return string.Join(" ", parts);
    }

    private static string? TryReadProcessOutput(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                return null;
            }

            var stderr = process.StandardError.ReadToEnd();
            var stdout = process.StandardOutput.ReadToEnd();
            var combined = string.Join(" ", new[] { stderr, stdout }
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text.Trim()));

            return string.IsNullOrWhiteSpace(combined)
                ? null
                : combined;
        }
        catch
        {
            return null;
        }
    }

    private static string? BuildExitCodeHint(int? exitCode)
    {
        if (exitCode == 133)
        {
            return "Exit code 133 commonly maps to SIGTRAP/abort on macOS; check process output and native crash reports under ~/Library/Logs/DiagnosticReports.";
        }

        return null;
    }

    private ProcessStartInfo BuildStartInfo(PluginManifest manifest, string? executable)
    {
        var pluginRoot = _entrypointResolver.GetPluginRoot(manifest.Id);
        executable ??= _entrypointResolver.ResolveEntrypoint(manifest);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = pluginRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        SanitizePluginProcessEnvironment(startInfo);
        return startInfo;
    }

    private static void SanitizePluginProcessEnvironment(ProcessStartInfo startInfo)
    {
        // Prevent host/debug dynamic-loader variables from destabilizing plugin startup
        // (for example when launched under flutter run on macOS).
        startInfo.Environment.Remove("DYLD_INSERT_LIBRARIES");
        startInfo.Environment.Remove("DYLD_LIBRARY_PATH");
        startInfo.Environment.Remove("DYLD_FRAMEWORK_PATH");
        startInfo.Environment.Remove("DYLD_FALLBACK_LIBRARY_PATH");
        startInfo.Environment.Remove("DYLD_FALLBACK_FRAMEWORK_PATH");

        // Plugin process should boot independently of host runtime probing.
        startInfo.Environment.Remove("DOTNET_STARTUP_HOOKS");
        startInfo.Environment.Remove("DOTNET_ADDITIONAL_DEPS");
    }

    private bool TryResolveEntrypoint(PluginManifest manifest, out string executable)
    {
        try
        {
            executable = _entrypointResolver.ResolveEntrypoint(manifest);
            return true;
        }
        catch (InvalidOperationException)
        {
            executable = string.Empty;
            return false;
        }
    }

    private static void ApplyPluginPort(PluginManifest manifest, ProcessStartInfo startInfo)
    {
        if (string.IsNullOrWhiteSpace(manifest.Endpoint))
        {
            return;
        }

        if (!Uri.TryCreate(manifest.Endpoint, UriKind.Absolute, out var address))
        {
            return;
        }

        if (address.Port <= 0)
        {
            return;
        }

        startInfo.Environment["EMMA_PLUGIN_PORT"] = address.Port.ToString();
    }

    private static void ApplyHostAuthToken(ProcessStartInfo startInfo, string hostAuthToken)
    {
        if (string.IsNullOrWhiteSpace(hostAuthToken))
        {
            return;
        }

        startInfo.Environment[HostAuthTokenEnvVar] = hostAuthToken;
    }

    private static string GenerateHostAuthToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private static string? ReadEnv(ProcessStartInfo startInfo, string key)
    {
        return startInfo.Environment.TryGetValue(key, out var value)
            ? value
            : null;
    }

    private void AttachLogCapture(string pluginId, Process process)
    {
        if (!_logs.TryGetValue(pluginId, out var buffer))
        {
            return;
        }

        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                buffer.Append(args.Data);
                ForwardLogLine(pluginId, args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                buffer.Append(args.Data);
                ForwardLogLine(pluginId, args.Data);
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private void ForwardLogLine(string pluginId, string line)
    {
        var correlationId = TryExtractCorrelationId(line);
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            _logger.LogInformation("Plugin log {PluginId} {Line}", pluginId, line);
            return;
        }

        _logger.LogInformation("Plugin log {PluginId} {CorrelationId} {Line}", pluginId, correlationId, line);
    }

    private static string? TryExtractCorrelationId(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var match = CorrelationIdRegex.Match(line);
        if (match.Success)
        {
            return match.Groups["id"].Value;
        }

        return null;
    }


    private static async Task<bool> WaitForEndpointAsync(
        Uri address,
        TimeSpan timeout,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        var stopAt = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < stopAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await TryConnectAsync(address, cancellationToken))
            {
                return true;
            }

            await timer.WaitForNextTickAsync(cancellationToken);
        }

        return false;
    }

    private static async Task<bool> TryConnectAsync(Uri address, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address.Host) || address.Port <= 0)
        {
            return false;
        }

        if (string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            if (await TryConnectAsync(IPAddress.Loopback, address.Port, cancellationToken))
            {
                return true;
            }

            if (Socket.OSSupportsIPv6 && await TryConnectAsync(IPAddress.IPv6Loopback, address.Port, cancellationToken))
            {
                return true;
            }

            return false;
        }

        return await TryConnectAsync(address.Host, address.Port, cancellationToken);
    }

    private static async Task<bool> TryConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var completed = await Task.WhenAny(connectTask, timeoutTask);

            if (completed != connectTask)
            {
                return false;
            }

            await connectTask;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(address, port);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var completed = await Task.WhenAny(connectTask, timeoutTask);

            if (completed != connectTask)
            {
                return false;
            }

            await connectTask;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
