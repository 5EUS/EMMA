using System.Net;
using System.Net.Sockets;

namespace EMMA.PluginHost.Plugins;

public sealed class PluginEndpointAllocator
{
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