using System.Net;
using System.Net.Sockets;

namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Assigns loopback endpoints to plugins that require one but do not declare a fixed address.
/// </summary>
public sealed class PluginEndpointAllocator
{
    /// <summary>
    /// Ensures that a gRPC plugin manifest has a concrete loopback endpoint.
    /// </summary>
    /// <param name="manifest">The plugin manifest to inspect.</param>
    /// <returns>The original manifest when no endpoint change is needed; otherwise, a copy with an allocated endpoint.</returns>
    public PluginManifest EnsureEndpoint(PluginManifest manifest)
    {
        if (!string.Equals(manifest.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            return manifest;
        }

        if (!string.IsNullOrWhiteSpace(manifest.Endpoint))
        {
            return manifest;
        }

        var port = AllocatePort();
        var endpoint = $"http://127.0.0.1:{port}";

        return manifest with { Endpoint = endpoint };
    }

    private static int AllocatePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}