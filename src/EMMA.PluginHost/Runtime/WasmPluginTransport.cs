using EMMA.Domain;
using EMMA.PluginHost.Instances;
using EMMA.PluginHost.Policy;
using EMMA.WasmRuntime;

namespace EMMA.PluginHost.Runtime;

/// <summary>Runs plugins as WebAssembly components in the native host: the only transport so far.</summary>
/// <param name="runtime">The native Wasm host shared by every plugin.</param>
public sealed class WasmPluginTransport(WasmRuntimeHost runtime) : IPluginTransport
{
    /// <inheritdoc/>
    public string Name => "wasm";

    /// <inheritdoc/>
    public Task<Outcome<IPluginInstance>> OpenAsync(PluginLaunch launch, CancellationToken cancellationToken)
    {
        WasmPluginConfig config = new(
            launch.EntrypointPath,
            new WasmLimits(launch.Manifest.Limits.CpuBudgetMs, launch.Manifest.Limits.MemoryMb),
            SettingNetworkHosts.Effective(launch.Manifest.Permissions, launch.Settings),
            launch.Manifest.Permissions.Cache,
            launch.Settings,
            AllowLocalNetwork: SettingNetworkHosts.AllowsLocalNetwork(launch.Manifest.Permissions, launch.Settings));
        Outcome<WasmPlugin> opened = runtime.Open(config);
        if (!opened.IsSuccess)
        {
            return Task.FromResult(Outcome.Failure<IPluginInstance>(opened.Error));
        }

        // Ownership of the instance passes to the caller inside the Outcome, which the disposal analyzers cannot follow.
#pragma warning disable CA2000
        WasmPluginInstance instance = new(opened.Value);
#pragma warning restore CA2000
        return Task.FromResult(Outcome.Success<IPluginInstance>(instance));
    }
}
