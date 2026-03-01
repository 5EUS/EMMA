using System.Net;
using EMMA.Contracts.Plugins;
using EMMA.PluginHost.Configuration;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using EMMA.PluginHost.Sandboxing;
using EMMA.PluginHost.Services;
using Grpc.Core;

namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Loads plugin manifests and performs a gRPC handshake to record status.
/// </summary>
public sealed class PluginHandshakeService(
    PluginManifestLoader loader,
    PluginRegistry registry,
    IPluginSandboxManager sandboxManager,
    PluginProcessManager processManager,
    PluginPermissionSanitizer permissionSanitizer,
    PluginEndpointAllocator endpointAllocator,
    IWasmPluginRuntimeHost wasmRuntimeHost,
    IOptions<PluginHostOptions> options,
    ILogger<PluginHandshakeService> logger)
{
    private readonly PluginManifestLoader _loader = loader;
    private readonly PluginRegistry _registry = registry;
    private readonly IPluginSandboxManager _sandboxManager = sandboxManager;
    private readonly PluginProcessManager _processManager = processManager;
    private readonly PluginPermissionSanitizer _permissionSanitizer = permissionSanitizer;
    private readonly PluginEndpointAllocator _endpointAllocator = endpointAllocator;
    private readonly IWasmPluginRuntimeHost _wasmRuntimeHost = wasmRuntimeHost;
    private readonly PluginHostOptions _options = options.Value;
    private readonly ILogger<PluginHandshakeService> _logger = logger;

    /// <summary>
    /// Loads all manifests and attempts a handshake for each plugin.
    /// </summary>
    public async Task HandshakeAllAsync(CancellationToken cancellationToken)
    {
        var manifests = await _loader.LoadManifestsAsync(cancellationToken);
        foreach (var manifest in manifests)
        {
            var updated = _endpointAllocator.EnsureEndpoint(manifest);
            _registry.Upsert(updated, PluginHandshakeDefaults.NotChecked(), _registry.GetRuntime(updated));
        }

        if (!_options.HandshakeOnStartup)
        {
            _logger.LogInformation("Plugin handshake is disabled by configuration.");
            return;
        }

        foreach (var manifest in manifests)
        {
            var updated = _endpointAllocator.EnsureEndpoint(manifest);
            await _sandboxManager.PrepareAsync(updated, cancellationToken);
            var runtime = await _processManager.EnsureStartedAsync(
                updated,
                _registry.GetRuntime(updated),
                cancellationToken);
            _registry.UpdateRuntime(updated, runtime);

            var status = await HandshakeAsync(updated, runtime, cancellationToken);
            runtime = _registry.GetRuntime(updated);
            _registry.Upsert(updated, status, runtime);
        }
    }

    /// <summary>
    /// Reloads manifests and performs a handshake regardless of startup settings.
    /// </summary>
    public async Task RescanAsync(CancellationToken cancellationToken)
    {
        var manifests = await _loader.LoadManifestsAsync(cancellationToken);
        var manifestIds = manifests.Select(manifest => manifest.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var snapshot = _registry.GetSnapshot();
        foreach (var record in snapshot)
        {
            if (!manifestIds.Contains(record.Manifest.Id))
            {
                await _processManager.StopAsync(record.Manifest.Id, cancellationToken);
                _registry.UpdateRuntime(record.Manifest, PluginRuntimeStatus.Stopped());
            }
        }

        foreach (var manifest in manifests)
        {
            var updated = _endpointAllocator.EnsureEndpoint(manifest);
            await _sandboxManager.PrepareAsync(updated, cancellationToken);
            var runtime = await _processManager.EnsureStartedAsync(
                updated,
                _registry.GetRuntime(updated),
                cancellationToken);
            _registry.UpdateRuntime(updated, runtime);

            var status = await HandshakeAsync(updated, runtime, cancellationToken);
            runtime = _registry.GetRuntime(updated);
            _registry.Upsert(updated, status, runtime);
        }
    }

    public async Task<PluginHandshakeStatus> HandshakeSingleAsync(
        PluginManifest manifest,
        CancellationToken cancellationToken)
    {
        var updated = _endpointAllocator.EnsureEndpoint(manifest);
        var runtime = _registry.GetRuntime(updated);
        var status = await HandshakeAsync(updated, runtime, cancellationToken);
        runtime = _registry.GetRuntime(updated);
        _registry.Upsert(updated, status, runtime);
        return status;
    }

    private async Task<PluginHandshakeStatus> HandshakeAsync(
        PluginManifest manifest,
        PluginRuntimeStatus runtime,
        CancellationToken cancellationToken)
    {
        if (runtime.State is PluginRuntimeState.Quarantined or PluginRuntimeState.Disabled)
        {
            return Failed($"Plugin is {runtime.State.ToString().ToLowerInvariant()}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Protocol))
        {
            return Failed("Missing protocol.");
        }

        if (_wasmRuntimeHost.IsWasmPlugin(manifest))
        {
            try
            {
                return await _wasmRuntimeHost.HandshakeAsync(manifest, cancellationToken);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(ex, "WASM component handshake failed for plugin {PluginId}", manifest.Id);
                }

                return Failed(ex.Message);
            }
        }

        if (!string.Equals(manifest.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            return Failed($"Unsupported protocol: {manifest.Protocol}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Endpoint))
        {
            return Failed("Missing endpoint.");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.HandshakeTimeoutSeconds));

            var address = new Uri(manifest.Endpoint, UriKind.Absolute);
            using var httpClient = CreateHttpClient(address);
            using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpClient = httpClient
            });

            var client = new PluginControl.PluginControlClient(channel);
            var correlationId = CreateCorrelationId();
            var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(_options.HandshakeTimeoutSeconds);
            var headers = CreateHeaders(correlationId);
            var context = CreateRequestContext(correlationId, deadlineUtc);
            var health = await client.GetHealthAsync(new HealthRequest { Context = context }, headers: headers, cancellationToken: cts.Token);
            var capabilities = await client.GetCapabilitiesAsync(new CapabilitiesRequest { Context = context }, headers: headers, cancellationToken: cts.Token);

            var caps = capabilities.Capabilities.ToArray();
            var budgets = capabilities.Budgets;
            var permissions = capabilities.Permissions;
            var manifestCaps = manifest.Capabilities;
            var manifestPermissions = manifest.Permissions;
            IReadOnlyList<string> effectivePaths;
            if (manifestPermissions?.Paths is not null)
            {
                effectivePaths = manifestPermissions.Paths;
            }
            else
            {
                effectivePaths = _permissionSanitizer.SanitizePaths(
                    manifest.Id,
                    permissions?.Paths.ToArray() ?? [],
                    "grpc") ?? [];
            }
            var message = string.IsNullOrWhiteSpace(health.Message) ? "Handshake ok" : health.Message;

            return new PluginHandshakeStatus(
                true,
                message,
                health.Version,
                DateTimeOffset.UtcNow,
                caps,
                manifestCaps?.CpuBudgetMs ?? budgets?.CpuBudgetMs ?? 0,
                manifestCaps?.MemoryMb ?? budgets?.MemoryMb ?? 0,
                manifestPermissions?.Domains?.ToArray() ?? permissions?.Domains.ToArray() ?? [],
                [.. effectivePaths]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await HandleTimeoutAsync(manifest, runtime, cancellationToken);
            return Failed("Handshake timed out.");
        }
        catch (RpcException ex) when (
            ex.StatusCode == StatusCode.DeadlineExceeded
            || ex.StatusCode == StatusCode.Cancelled)
        {
            await HandleTimeoutAsync(manifest, runtime, cancellationToken);
            return Failed("Handshake timed out.");
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Handshake failed for plugin {PluginId}", manifest.Id);
            }
            return Failed(ex.Message);
        }
    }

    private async Task HandleTimeoutAsync(
        PluginManifest manifest,
        PluginRuntimeStatus runtime,
        CancellationToken cancellationToken)
    {
        await _processManager.StopAsync(manifest.Id, cancellationToken);
        var updated = _processManager.RecordTimeout(runtime);
        _registry.UpdateRuntime(manifest, updated);
    }

    private static HttpClient CreateHttpClient(Uri address)
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true
        };

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = address,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        return httpClient;
    }

    private static string CreateCorrelationId() => Guid.NewGuid().ToString("n");

    private static Metadata CreateHeaders(string correlationId) => new()
    {
        { "x-correlation-id", correlationId }
    };

    private static RequestContext CreateRequestContext(string correlationId, DateTimeOffset deadlineUtc)
    {
        return new RequestContext
        {
            CorrelationId = correlationId,
            DeadlineUtc = deadlineUtc.ToString("O")
        };
    }

    private static PluginHandshakeStatus Failed(string message)
    {
        return new PluginHandshakeStatus(false, message, null, DateTimeOffset.UtcNow, [], 0, 0, [], []);
    }
}
