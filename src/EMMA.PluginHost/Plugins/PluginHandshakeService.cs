using System.Net;
using EMMA.Contracts.Plugins;
using EMMA.PluginHost.Configuration;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Loads plugin manifests and performs a gRPC handshake to record status.
/// </summary>
public sealed class PluginHandshakeService(
    PluginManifestLoader loader,
    PluginRegistry registry,
    IOptions<PluginHostOptions> options,
    ILogger<PluginHandshakeService> logger)
{
    private readonly PluginManifestLoader _loader = loader;
    private readonly PluginRegistry _registry = registry;
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
            var status = await HandshakeAsync(manifest, cancellationToken);
            _registry.Upsert(new PluginRecord(manifest, status));
        }
    }

    private async Task<PluginHandshakeStatus> HandshakeAsync(PluginManifest manifest, CancellationToken cancellationToken)
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
            var message = string.IsNullOrWhiteSpace(health.Message) ? "Handshake ok" : health.Message;

            return new PluginHandshakeStatus(true, message, health.Version, DateTimeOffset.UtcNow, caps);
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
        return new PluginHandshakeStatus(false, message, null, DateTimeOffset.UtcNow, []);
    }
}
