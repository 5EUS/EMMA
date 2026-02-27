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

        var runtimeState = record.Runtime.State;
        var staleRunningState = runtimeState == PluginRuntimeState.Running
            && !_processManager.IsProcessRunning(record.Manifest.Id);
        var shouldEnsureRuntime = runtimeState is PluginRuntimeState.Unknown
            or PluginRuntimeState.Starting
            or PluginRuntimeState.Stopped
            or PluginRuntimeState.Crashed
            or PluginRuntimeState.Timeout
            || staleRunningState;
        var shouldRehandshake = !record.Status.Success
            && runtimeState is PluginRuntimeState.Running or PluginRuntimeState.External;

        if (shouldEnsureRuntime || shouldRehandshake)
        {
            var updated = _endpointAllocator.EnsureEndpoint(record.Manifest);
            await _sandboxManager.PrepareAsync(updated, cancellationToken);

            var runtime = await _processManager.EnsureStartedAsync(
                updated,
                _registry.GetRuntime(updated),
                cancellationToken);
            _registry.UpdateRuntime(updated, runtime);

            if (runtime.State is PluginRuntimeState.Running or PluginRuntimeState.External)
            {
                await _handshakeService.HandshakeSingleAsync(updated, cancellationToken);
            }

            snapshot = _registry.GetSnapshot();
            record = snapshot.FirstOrDefault(item =>
                string.Equals(item.Manifest.Id, updated.Id, StringComparison.OrdinalIgnoreCase));
        }

        if (record is null)
        {
            return (null, null, Results.NotFound(new { message = "No matching plugin record found." }));
        }

        if (string.IsNullOrWhiteSpace(record.Manifest.Protocol))
        {
            return (record, null, Results.Problem("Plugin manifest protocol is missing."));
        }

        if (record.Runtime.State is not (PluginRuntimeState.Running or PluginRuntimeState.External))
        {
            return (
                record,
                null,
                Results.Problem(
                    $"Plugin '{record.Manifest.Id}' runtime is '{record.Runtime.State}' ({record.Runtime.LastErrorCode ?? "no-code"}: {record.Runtime.LastErrorMessage ?? "no-details"}).",
                    statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        if (record.Runtime.State != PluginRuntimeState.External && !record.Status.Success)
        {
            return (
                record,
                null,
                Results.Problem(
                    $"Plugin '{record.Manifest.Id}' handshake failed: {record.Status.Message}",
                    statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        if (!string.Equals(record.Manifest.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            return (record, null, Results.Problem($"Unsupported plugin protocol: {record.Manifest.Protocol}."));
        }

        if (string.IsNullOrWhiteSpace(record.Manifest.Endpoint))
        {
            return (record, null, Results.Problem("Plugin manifest endpoint is missing."));
        }

        if (!Uri.TryCreate(record.Manifest.Endpoint, UriKind.Absolute, out var address))
        {
            return (record, null, Results.Problem("Plugin manifest endpoint is invalid."));
        }

        return (record, address, null);
    }
}
