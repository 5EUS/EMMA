using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using EMMA.PluginHost.Configuration;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Minimal process supervisor for plugin startup and shutdown.
/// TODO: Deprecate once a dedicated supervisor service is implemented.
/// </summary>
public sealed class PluginProcessManager(IOptions<PluginHostOptions> options, ILogger<PluginProcessManager> logger)
{
    private sealed record ProcessHandle(Process Process, string StartupCommand, DateTimeOffset StartedAt);

    private readonly PluginHostOptions _options = options.Value;
    private readonly ILogger<PluginProcessManager> _logger = logger;
    private readonly Dictionary<string, ProcessHandle> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public async Task<PluginRuntimeStatus> EnsureStartedAsync(
        PluginManifest manifest,
        PluginRuntimeStatus current,
        CancellationToken cancellationToken)
    {
        if (manifest.Entry is null)
        {
            return current.WithState(PluginRuntimeState.Disabled, "entry-missing", "Plugin manifest entry is missing.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Entry.Startup))
        {
            // External plugin endpoint; we do not manage its lifecycle here.
            return current.State == PluginRuntimeState.Unknown
                ? PluginRuntimeStatus.External()
                : current;
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

        if (TryGetProcess(manifest.Id, out var existing))
        {
            if (!existing.Process.HasExited)
            {
                return current.WithState(PluginRuntimeState.Running, current.LastErrorCode, current.LastErrorMessage);
            }

            RemoveProcess(manifest.Id);
            var exitCode = SafeExitCode(existing.Process);
            return current.WithState(PluginRuntimeState.Crashed, "process-exited", "Plugin process exited.", exitCode);
        }

        var startInfo = BuildStartInfo(manifest.Entry.Startup);
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            return current.WithState(PluginRuntimeState.Crashed, "start-failed", "Failed to start plugin process.");
        }

        AddProcess(manifest.Id, process, manifest.Entry.Startup);

        if (string.IsNullOrWhiteSpace(manifest.Entry.Endpoint)
            || !Uri.TryCreate(manifest.Entry.Endpoint, UriKind.Absolute, out var address))
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
        }
    }

    private void RemoveProcess(string pluginId)
    {
        lock (_lock)
        {
            _processes.Remove(pluginId);
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

    private ProcessStartInfo BuildStartInfo(string startup)
    {
        if (TryParseCommandLine(startup, out var fileName, out var arguments))
        {
            return new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false
            };
        }

        // TODO: Deprecate shell fallback once startup commands are structured.
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {startup}",
                UseShellExecute = false
            };
        }

        return new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = $"-c {startup}",
            UseShellExecute = false
        };
    }

    private static bool TryParseCommandLine(
        string command,
        out string fileName,
        out string arguments)
    {
        fileName = string.Empty;
        arguments = string.Empty;

        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var parts = new List<string>();
        var current = new List<char>();
        var inQuotes = false;

        foreach (var ch in command)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Count > 0)
                {
                    parts.Add(new string(current.ToArray()));
                    current.Clear();
                }
                continue;
            }

            current.Add(ch);
        }

        if (current.Count > 0)
        {
            parts.Add(new string(current.ToArray()));
        }

        if (parts.Count == 0)
        {
            return false;
        }

        fileName = parts[0];
        arguments = string.Join(' ', parts.Skip(1));
        return true;
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
