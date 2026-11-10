using EMMA.Domain;
using EMMA.PluginHost.Instances;
using EMMA.PluginHost.Runtime;
using EMMA.WasmRuntime;

namespace EMMA.Runtime;

/// <summary>
/// The Wasm transport, with the native host loaded on first use. A host that never opens a plugin (listing
/// what is installed, say) neither needs nor fails on the native library, and a missing one is reported
/// when a plugin is first opened, with the reason.
/// </summary>
internal sealed class LazyWasmTransport : IPluginTransport, IDisposable
{
    private readonly Lazy<Loaded> loaded = new(() => new Loaded(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <inheritdoc/>
    public string Name => "wasm";

    /// <inheritdoc/>
    public Task<Outcome<IPluginInstance>> OpenAsync(PluginLaunch launch, CancellationToken cancellationToken)
    {
        try
        {
            return this.loaded.Value.Transport.OpenAsync(launch, cancellationToken);
        }
        catch (WasmRuntimeException ex)
        {
            return Task.FromResult(Outcome.Failure<IPluginInstance>(ErrorCode.Internal, ex.Message));
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.loaded.IsValueCreated)
        {
            this.loaded.Value.Dispose();
        }
    }

    private sealed class Loaded : IDisposable
    {
        // Apple platforms forbid making memory executable, so the engine cannot JIT there; it compiles for the
        // Pulley interpreter instead. Elsewhere the engine uses the machine's own code.
        private readonly WasmRuntimeHost host = WasmRuntimeHost.Create(
            OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() ? new WasmRuntimeOptions("pulley64") : null);

        public Loaded() => this.Transport = new WasmPluginTransport(this.host);

        public WasmPluginTransport Transport { get; }

        public void Dispose() => this.host.Dispose();
    }
}
