using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;
using EMMA.PluginHost.Configuration;
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
    private static readonly Regex CorrelationIdRegex = new(
        "CorrelationId\\s*[:=]\\s*(?<id>[A-Za-z0-9-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private sealed record ProcessHandle(Process Process, string StartupCommand, DateTimeOffset StartedAt);
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
    private readonly Dictionary<string, LogBuffer> _logs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public async Task<PluginRuntimeStatus> EnsureStartedAsync(
        PluginManifest manifest,
        PluginRuntimeStatus current,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifest.Protocol))
        {
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
                return current.WithState(PluginRuntimeState.Running, current.LastErrorCode, current.LastErrorMessage);
            }

            RemoveProcess(manifest.Id);
        }

        var executable = (string?)null;
        var hasEndpoint = !string.IsNullOrWhiteSpace(manifest.Endpoint);
        if (hasEndpoint && !TryResolveEntrypoint(manifest, out executable))
        {
            return current.State == PluginRuntimeState.Unknown
                ? PluginRuntimeStatus.External()
                : current;
        }

        await _sandboxManager.PrepareAsync(manifest, cancellationToken);

        var startInfo = BuildStartInfo(manifest, executable);
        ApplyPluginPort(manifest, startInfo);
        startInfo = _sandboxManager.ApplyToStartInfo(manifest, startInfo);
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

        if (!process.Start())
        {
            return current.WithState(PluginRuntimeState.Crashed, "start-failed", "Failed to start plugin process.");
        }

        if (process.HasExited)
        {
            var exitCode = SafeExitCode(process);
            return current.WithState(
                PluginRuntimeState.Crashed,
                "process-exited",
                "Plugin process exited immediately.",
                exitCode);
        }

        AddProcess(manifest.Id, process, startInfo.FileName ?? string.Empty);
        AttachLogCapture(manifest.Id, process);

        await _sandboxManager.EnforceAsync(manifest, process, cancellationToken);

        if (string.IsNullOrWhiteSpace(manifest.Endpoint)
            || !Uri.TryCreate(manifest.Endpoint, UriKind.Absolute, out var address))
        {
            return PluginRuntimeStatus.Running();
        }

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
                return current.WithState(
                    PluginRuntimeState.Crashed,
                    "process-exited",
                    "Plugin process exited during startup.",
                    exitCode);
            }
            await StopAsync(manifest.Id, cancellationToken);
            return current.WithRetry(
                current.RetryCount + 1,
                DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, _options.TimeoutBackoffSeconds) * (current.RetryCount + 1)),
                "startup-timeout",
                "Plugin did not become ready in time.");
        }

        return PluginRuntimeStatus.Running();
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

        try
        {
            if (!handle.Process.HasExited)
            {
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

    private void RemoveProcess(string pluginId)
    {
        lock (_lock)
        {
            _processes.Remove(pluginId);
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

    private ProcessStartInfo BuildStartInfo(PluginManifest manifest, string? executable)
    {
        var pluginRoot = _entrypointResolver.GetPluginRoot(manifest.Id);
        executable ??= _entrypointResolver.ResolveEntrypoint(manifest);
        return new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = pluginRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
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
