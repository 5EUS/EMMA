using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Sandboxing;

namespace EMMA.PluginHost.Services;

public sealed record PluginResolutionError(string Message, int StatusCode);

public sealed class PluginResolutionService(
    PluginRegistry registry,
    PluginManifestLoader manifestLoader,
    IPluginSandboxManager sandboxManager,
    PluginProcessManager processManager,
    PluginHandshakeService handshakeService,
    IWasmPluginRuntimeHost wasmRuntimeHost,
    Plugins.PluginEndpointAllocator endpointAllocator)
{
    private readonly PluginRegistry _registry = registry;
    private readonly PluginManifestLoader _manifestLoader = manifestLoader;
    private readonly IPluginSandboxManager _sandboxManager = sandboxManager;
    private readonly PluginProcessManager _processManager = processManager;
    private readonly PluginHandshakeService _handshakeService = handshakeService;
    private readonly IWasmPluginRuntimeHost _wasmRuntimeHost = wasmRuntimeHost;
    private readonly Plugins.PluginEndpointAllocator _endpointAllocator = endpointAllocator;

    public async Task<(PluginRecord? Record, Uri? Address, PluginResolutionError? Error)> ResolveAsync(
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
                return (null, null, new PluginResolutionError("No matching plugin record found.", 404));
            }

            var updated = ResolveEndpointIfNeeded(manifest);
            if (_wasmRuntimeHost.IsWasmPlugin(updated))
            {
                _registry.Upsert(updated, PluginHandshakeDefaults.NotChecked(), _registry.GetRuntime(updated));
                _registry.UpdateRuntime(updated, PluginRuntimeStatus.External());
                await _handshakeService.HandshakeSingleAsync(updated, cancellationToken);
            }
            else
            {
                await _sandboxManager.PrepareAsync(updated, cancellationToken);
                var runtime = await _processManager.EnsureStartedAsync(
                    updated,
                    _registry.GetRuntime(updated),
                    cancellationToken);
                _registry.UpdateRuntime(updated, runtime);
                await _handshakeService.HandshakeSingleAsync(updated, cancellationToken);
            }

            snapshot = _registry.GetSnapshot();
            record = snapshot.FirstOrDefault(item =>
                string.Equals(item.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        }

        if (record is null)
        {
            return (null, null, new PluginResolutionError("No matching plugin record found.", 404));
        }

        var runtimeState = record.Runtime.State;
        var isWasm = _wasmRuntimeHost.IsWasmPlugin(record.Manifest);

        var staleRunningState = !isWasm
            && runtimeState == PluginRuntimeState.Running
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
            var updated = ResolveEndpointIfNeeded(record.Manifest);
            if (isWasm)
            {
                _registry.UpdateRuntime(updated, PluginRuntimeStatus.External());
                await _handshakeService.HandshakeSingleAsync(updated, cancellationToken);
            }
            else
            {
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
            }

            snapshot = _registry.GetSnapshot();
            record = snapshot.FirstOrDefault(item =>
                string.Equals(item.Manifest.Id, updated.Id, StringComparison.OrdinalIgnoreCase));
        }

        if (record is null)
        {
            return (null, null, new PluginResolutionError("No matching plugin record found.", 404));
        }

        if (string.IsNullOrWhiteSpace(record.Manifest.Protocol))
        {
            return (record, null, new PluginResolutionError("Plugin manifest protocol is missing.", 500));
        }

        if (isWasm)
        {
            if (!record.Status.Success)
            {
                return (
                    record,
                    null,
                    new PluginResolutionError(
                        $"Plugin '{record.Manifest.Id}' handshake failed: {record.Status.Message}",
                        503));
            }

            return (record, null, null);
        }

        if (record.Runtime.State is not (PluginRuntimeState.Running or PluginRuntimeState.External))
        {
            return (
                record,
                null,
                new PluginResolutionError(
                    $"Plugin '{record.Manifest.Id}' runtime is '{record.Runtime.State}' ({record.Runtime.LastErrorCode ?? "no-code"}: {record.Runtime.LastErrorMessage ?? "no-details"}).",
                    503));
        }

        if (record.Runtime.State != PluginRuntimeState.External && !record.Status.Success)
        {
            return (
                record,
                null,
                new PluginResolutionError(
                    $"Plugin '{record.Manifest.Id}' handshake failed: {record.Status.Message}",
                    503));
        }

        if (!string.Equals(record.Manifest.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            return (record, null, new PluginResolutionError($"Unsupported plugin protocol: {record.Manifest.Protocol}.", 500));
        }

        if (string.IsNullOrWhiteSpace(record.Manifest.Endpoint))
        {
            return (record, null, new PluginResolutionError("Plugin manifest endpoint is missing.", 500));
        }

        if (!Uri.TryCreate(record.Manifest.Endpoint, UriKind.Absolute, out var address))
        {
            return (record, null, new PluginResolutionError("Plugin manifest endpoint is invalid.", 500));
        }

        return (record, address, null);
    }

    private PluginManifest ResolveEndpointIfNeeded(PluginManifest manifest)
    {
        if (_wasmRuntimeHost.IsWasmPlugin(manifest))
        {
            return manifest;
        }

        return _endpointAllocator.EnsureEndpoint(manifest);
    }
}
