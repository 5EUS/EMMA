using System.Net;
using System.Net.Sockets;

namespace EMMA.PluginHost.Plugins;

public sealed class PluginEndpointAllocator
{
    public PluginManifest EnsureEndpoint(PluginManifest manifest)
    {
        if (manifest.Entry is null)
        {
            return manifest;
        }

        if (!string.Equals(manifest.Entry.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            return manifest;
        }

        var port = AllocatePort();
        var endpoint = $"http://127.0.0.1:{port}";

        var updatedEntry = manifest.Entry with { Endpoint = endpoint };
        return manifest with { Entry = updatedEntry };
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