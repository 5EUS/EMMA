using System.Net;
using EMMA.Contracts.Plugins;
using EMMA.PluginHost.Configuration;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using EMMA.PluginHost.Sandboxing;

namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Loads plugin manifests and performs a gRPC handshake to record status.
/// </summary>
public sealed class PluginHandshakeService(
    PluginManifestLoader loader,
    PluginRegistry registry,
    IPluginSandboxManager sandboxManager,
    PluginProcessManager processManager,
    IOptions<PluginHostOptions> options,
    ILogger<PluginHandshakeService> logger)
{
    private readonly PluginManifestLoader _loader = loader;
    private readonly PluginRegistry _registry = registry;
    private readonly IPluginSandboxManager _sandboxManager = sandboxManager;
    private readonly PluginProcessManager _processManager = processManager;
    private readonly PluginHostOptions _options = options.Value;
    private readonly ILogger<PluginHandshakeService> _logger = logger;

    /// <summary>
    /// Loads all manifests and attempts a handshake for each plugin.
    /// </summary>
    public async Task HandshakeAllAsync(CancellationToken cancellationToken)
    {
        if (!_options.HandshakeOnStartup)
        {
            _logger.LogInformation("Plugin handshake is disabled by configuration.");
            return;
        }

        var manifests = await _loader.LoadManifestsAsync(cancellationToken);
        foreach (var manifest in manifests)
        {
            await _sandboxManager.PrepareAsync(manifest, cancellationToken);
            var runtime = await _processManager.EnsureStartedAsync(
                manifest,
                _registry.GetRuntime(manifest),
                cancellationToken);
            _registry.UpdateRuntime(manifest, runtime);

            var status = await HandshakeAsync(manifest, runtime, cancellationToken);
            _registry.Upsert(manifest, status, runtime);
        }
    }

    /// <summary>
    /// Reloads manifests and performs a handshake regardless of startup settings.
    /// </summary>
    public async Task RescanAsync(CancellationToken cancellationToken)
    {
        var manifests = await _loader.LoadManifestsAsync(cancellationToken);
        foreach (var manifest in manifests)
        {
            await _sandboxManager.PrepareAsync(manifest, cancellationToken);
            var runtime = await _processManager.EnsureStartedAsync(
                manifest,
                _registry.GetRuntime(manifest),
                cancellationToken);
            _registry.UpdateRuntime(manifest, runtime);

            var status = await HandshakeAsync(manifest, runtime, cancellationToken);
            _registry.Upsert(manifest, status, runtime);
        }
    }

    private async Task<PluginHandshakeStatus> HandshakeAsync(
        PluginManifest manifest,
        PluginRuntimeStatus runtime,
        CancellationToken cancellationToken)
    {
        if (manifest.Entry is null)
        {
            return Failed("Missing entry section.");
        }

        if (!string.Equals(manifest.Entry.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            return Failed($"Unsupported protocol: {manifest.Entry.Protocol}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Entry.Endpoint))
        {
            return Failed("Missing entry endpoint.");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.HandshakeTimeoutSeconds));

            var address = new Uri(manifest.Entry.Endpoint, UriKind.Absolute);
            using var httpClient = CreateHttpClient(address);
            using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpClient = httpClient
            });

            var client = new PluginControl.PluginControlClient(channel);
            var health = await client.GetHealthAsync(new HealthRequest(), cancellationToken: cts.Token);
            var capabilities = await client.GetCapabilitiesAsync(new CapabilitiesRequest(), cancellationToken: cts.Token);

            var caps = capabilities.Capabilities.ToArray();
            var budgets = capabilities.Budgets;
            var permissions = capabilities.Permissions;
            var manifestCaps = manifest.Capabilities;
            var manifestPermissions = manifest.Permissions;
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
                manifestPermissions?.Paths?.ToArray() ?? permissions?.Paths.ToArray() ?? []);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await HandleTimeoutAsync(manifest, runtime, cancellationToken);
            return Failed("Handshake timed out.");
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.DeadlineExceeded)
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

    private static PluginHandshakeStatus Failed(string message)
    {
        return new PluginHandshakeStatus(false, message, null, DateTimeOffset.UtcNow, [], 0, 0, [], []);
    }
}
