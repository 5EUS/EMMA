using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Sandboxing;

namespace EMMA.PluginHost.Services;

public sealed class PluginResolutionService(
    PluginRegistry registry,
    PluginManifestLoader manifestLoader,
    IPluginSandboxManager sandboxManager,
    PluginProcessManager processManager,
    PluginHandshakeService handshakeService,
    Plugins.PluginEndpointAllocator endpointAllocator)
{
    private readonly PluginRegistry _registry = registry;
    private readonly PluginManifestLoader _manifestLoader = manifestLoader;
    private readonly IPluginSandboxManager _sandboxManager = sandboxManager;
    private readonly PluginProcessManager _processManager = processManager;
    private readonly PluginHandshakeService _handshakeService = handshakeService;
    private readonly Plugins.PluginEndpointAllocator _endpointAllocator = endpointAllocator;

    public async Task<(PluginRecord? Record, Uri? Address, IResult? Error)> ResolveAsync(
        string? pluginId,
        CancellationToken cancellationToken)
    {
        var snapshot = _registry.GetSnapshot();
        PluginRecord? record = null;
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            if (snapshot.Count > 0)
            {
                record = snapshot[0];
            }
        }
        else
        {
            record = snapshot.FirstOrDefault(item =>
                string.Equals(item.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        }

        if (record is null && !string.IsNullOrWhiteSpace(pluginId))
        {
            var manifests = await _manifestLoader.LoadManifestsAsync(cancellationToken);
            var manifest = manifests.FirstOrDefault(item =>
                string.Equals(item.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            if (manifest is null)
            {
                return (null, null, Results.NotFound(new { message = "No matching plugin record found." }));
            }

            var updated = _endpointAllocator.EnsureEndpoint(manifest);
            await _sandboxManager.PrepareAsync(updated, cancellationToken);
            var runtime = await _processManager.EnsureStartedAsync(
                updated,
                _registry.GetRuntime(updated),
                cancellationToken);
            _registry.UpdateRuntime(updated, runtime);
            await _handshakeService.HandshakeSingleAsync(updated, cancellationToken);

            snapshot = _registry.GetSnapshot();
            record = snapshot.FirstOrDefault(item =>
                string.Equals(item.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        }

        if (record is null)
        {
            return (null, null, Results.NotFound(new { message = "No matching plugin record found." }));
        }

        if (record.Manifest.Entry is null)
        {
            return (record, null, Results.Problem("Plugin manifest has no entry."));
        }

        if (!string.Equals(record.Manifest.Entry.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            return (record, null, Results.Problem($"Unsupported plugin protocol: {record.Manifest.Entry.Protocol}."));
        }

        if (string.IsNullOrWhiteSpace(record.Manifest.Entry.Endpoint))
        {
            return (record, null, Results.Problem("Plugin manifest entry is missing endpoint."));
        }

        if (!Uri.TryCreate(record.Manifest.Entry.Endpoint, UriKind.Absolute, out var address))
        {
            return (record, null, Results.Problem("Plugin manifest entry endpoint is invalid."));
        }

        return (record, address, null);
    }
}
