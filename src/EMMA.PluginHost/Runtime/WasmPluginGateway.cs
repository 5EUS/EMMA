using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.PluginHost.Instances;

namespace EMMA.PluginHost.Runtime;

/// <summary>
/// The Wasm implementation of <see cref="IPluginGateway"/>. It loads plugins on first use through the
/// <see cref="PluginLoader"/>, keeps them open, forwards their logs, and quarantines a plugin that keeps
/// crashing or running out of budget.
/// </summary>
/// <param name="loader">Verifies and opens plugins.</param>
/// <param name="registry">The installed plugins, for quarantining.</param>
/// <param name="logs">Receives what plugins log.</param>
/// <param name="time">The clock.</param>
/// <param name="options">The failure policy.</param>
public sealed class WasmPluginGateway(
    PluginLoader loader,
    IPluginRegistryPort registry,
    IPluginLogPort logs,
    TimeProvider time,
    WasmGatewayOptions options) : IPluginGateway, IAsyncDisposable
{
    private readonly Dictionary<PluginId, LoadedPlugin> plugins = [];
    private readonly Dictionary<PluginId, int> consecutiveTraps = [];
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <inheritdoc/>
    public async Task<Outcome<PluginInfo>> DescribeAsync(PluginId plugin, CancellationToken cancellationToken)
    {
        Outcome<LoadedPlugin> loaded = await this.LoadedAsync(plugin, cancellationToken).ConfigureAwait(false);
        return loaded.IsSuccess ? Outcome.Success(loaded.Value.Info) : Outcome.Failure<PluginInfo>(loaded.Error);
    }

    /// <inheritdoc/>
    public Task<Outcome<PluginNodePage>> SearchAsync(PluginId plugin, Query query, CancellationToken cancellationToken) =>
        this.CallAsync(plugin, (open, token) => open.SearchAsync(query, token), cancellationToken);

    /// <inheritdoc/>
    public Task<Outcome<PluginNodePage>> ChildrenAsync(PluginId plugin, LocalNodeId? parent, PageRequest request, CancellationToken cancellationToken) =>
        this.CallAsync(plugin, (open, token) => open.ChildrenAsync(parent, request, token), cancellationToken);

    /// <inheritdoc/>
    public Task<Outcome<IReadOnlyList<PluginNode>>> GetAsync(PluginId plugin, IReadOnlyList<LocalNodeId> ids, CancellationToken cancellationToken) =>
        this.CallAsync(plugin, (open, token) => open.GetAsync(ids, token), cancellationToken);

    /// <inheritdoc/>
    public Task<Outcome<IReadOnlyList<Choice>>> SuggestAsync(PluginId plugin, string filterId, string text, int limit, CancellationToken cancellationToken) =>
        this.CallAsync(plugin, (open, token) => open.SuggestAsync(filterId, text, limit, token), cancellationToken);

    /// <inheritdoc/>
    public Task<Outcome<IReadOnlyList<PluginNode>>> EnrichAsync(PluginId plugin, IReadOnlyList<LocalNodeId> ids, CancellationToken cancellationToken) =>
        this.CallAsync(plugin, (open, token) => open.EnrichAsync(ids, token), cancellationToken);

    /// <inheritdoc/>
    public Task<Outcome<IReadOnlyList<Asset>>> ResolveAsync(PluginId plugin, ResolveRequest request, CancellationToken cancellationToken) =>
        this.CallAsync(plugin, (open, token) => open.ResolveAsync(request, token), cancellationToken);

    /// <inheritdoc/>
    public Task<Outcome<Chunk>> ReadAsync(PluginId plugin, string token, ByteRange? range, CancellationToken cancellationToken) =>
        this.CallAsync(plugin, (open, cancel) => open.ReadAsync(token, range, cancel), cancellationToken);

    /// <summary>
    /// Closes a plugin so the next call loads it again, for example after its settings changed or it was
    /// updated. A call already running finishes first.
    /// </summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="cancellationToken">Cancels the wait for the gateway's lock.</param>
    /// <returns>A task that completes when the plugin is closed.</returns>
    public async Task UnloadAsync(PluginId plugin, CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.ForgetAsync(plugin).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (LoadedPlugin loaded in this.plugins.Values)
        {
            await loaded.Instance.DisposeAsync().ConfigureAwait(false);
        }

        this.plugins.Clear();
        this.gate.Dispose();
    }

    private async Task<Outcome<T>> CallAsync<T>(
        PluginId plugin,
        Func<IPluginInstance, CancellationToken, Task<PluginCall<T>>> call,
        CancellationToken cancellationToken)
    {
        Outcome<LoadedPlugin> loaded = await this.LoadedAsync(plugin, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Outcome.Failure<T>(loaded.Error);
        }

        PluginCall<T> result = await call(loaded.Value.Instance, cancellationToken).ConfigureAwait(false);
        await this.RecordAsync(plugin, result.Logs, result.Trapped, cancellationToken).ConfigureAwait(false);
        return result.Outcome;
    }

    private async Task<Outcome<LoadedPlugin>> LoadedAsync(PluginId plugin, CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.plugins.TryGetValue(plugin, out LoadedPlugin? existing))
            {
                return Outcome.Success(existing);
            }

            Outcome<LoadedPlugin> loaded = await loader.LoadAsync(plugin, cancellationToken).ConfigureAwait(false);
            if (loaded.IsSuccess)
            {
                this.plugins[plugin] = loaded.Value;
            }

            return loaded;
        }
        finally
        {
            this.gate.Release();
        }
    }

    private async Task RecordAsync(PluginId plugin, IReadOnlyList<PluginLogEntry> entries, bool trapped, CancellationToken cancellationToken)
    {
        if (entries.Count > 0)
        {
            logs.Write(plugin, entries);
        }

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int count = trapped ? this.consecutiveTraps.GetValueOrDefault(plugin) + 1 : 0;
            this.consecutiveTraps[plugin] = count;
            if (count >= options.MaxConsecutiveTraps)
            {
                await this.ForgetAsync(plugin).ConfigureAwait(false);
                await registry.SetStatusAsync(plugin, PluginStatus.Quarantined, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    private async Task ForgetAsync(PluginId plugin)
    {
        if (this.plugins.Remove(plugin, out LoadedPlugin? loaded))
        {
            await loaded.Instance.DisposeAsync().ConfigureAwait(false);
        }

        this.consecutiveTraps.Remove(plugin);
    }
}
