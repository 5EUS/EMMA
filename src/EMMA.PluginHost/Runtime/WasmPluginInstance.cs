using EMMA.Domain;
using EMMA.PluginHost.Instances;
using EMMA.WasmRuntime;

namespace EMMA.PluginHost.Runtime;

/// <summary>An <see cref="IPluginInstance"/> backed by a component open in the native Wasm host.</summary>
public sealed class WasmPluginInstance : IPluginInstance
{
    private static readonly IReadOnlySet<string> NoExtensions = new HashSet<string>(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> WithSearchAssist = new HashSet<string>(StringComparer.Ordinal) { PluginExtensions.SearchAssist };

    private readonly WasmPlugin plugin;

    /// <summary>Initializes a new instance of the <see cref="WasmPluginInstance"/> class.</summary>
    /// <param name="plugin">The open plugin; this instance takes ownership.</param>
    internal WasmPluginInstance(WasmPlugin plugin)
    {
        this.plugin = plugin;
        this.Extensions = plugin.SearchAssist ? WithSearchAssist : NoExtensions;
    }

    /// <inheritdoc/>
    public IReadOnlySet<string> Extensions { get; }

    /// <inheritdoc/>
    public Task<PluginCall<PluginInfo>> DescribeAsync(CancellationToken cancellationToken) => this.plugin.DescribeAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<PluginCall<PluginHealth>> HealthAsync(CancellationToken cancellationToken) => this.plugin.HealthAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<PluginCall<PluginNodePage>> SearchAsync(Query query, CancellationToken cancellationToken) => this.plugin.SearchAsync(query, cancellationToken);

    /// <inheritdoc/>
    public Task<PluginCall<PluginNodePage>> ChildrenAsync(LocalNodeId? parent, PageRequest request, CancellationToken cancellationToken) =>
        this.plugin.ChildrenAsync(parent, request, cancellationToken);

    /// <inheritdoc/>
    public Task<PluginCall<IReadOnlyList<PluginNode>>> GetAsync(IReadOnlyList<LocalNodeId> ids, CancellationToken cancellationToken) =>
        this.plugin.GetAsync(ids, cancellationToken);

    /// <inheritdoc/>
    public Task<PluginCall<IReadOnlyList<Asset>>> ResolveAsync(ResolveRequest request, CancellationToken cancellationToken) =>
        this.plugin.ResolveAsync(request, cancellationToken);

    /// <inheritdoc/>
    public Task<PluginCall<Chunk>> ReadAsync(string token, ByteRange? range, CancellationToken cancellationToken) =>
        this.plugin.ReadAsync(token, range, cancellationToken);

    /// <inheritdoc/>
    public Task<PluginCall<IReadOnlyList<string>>> SuggestAsync(string text, int limit, CancellationToken cancellationToken) =>
        this.plugin.SuggestAsync(text, limit, cancellationToken);

    /// <inheritdoc/>
    public Task<PluginCall<IReadOnlyList<PluginNode>>> EnrichAsync(IReadOnlyList<LocalNodeId> ids, CancellationToken cancellationToken) =>
        this.plugin.EnrichAsync(ids, cancellationToken);

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        // The instance owns the plugin it is constructed with (the transport hands it over); the analyzer sees only a constructor argument.
#pragma warning disable IDISP007
        this.plugin.Dispose();
#pragma warning restore IDISP007
        return ValueTask.CompletedTask;
    }
}
