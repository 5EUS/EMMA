using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.TestSupport;

/// <summary>
/// A scriptable <see cref="IPluginGateway"/>. Every operation answers "unsupported" until a
/// test assigns a handler, and every call is counted so tests can assert what reached the plugin.
/// </summary>
public sealed class FakePluginGateway : IPluginGateway
{
    private int describeCalls;
    private int searchCalls;
    private int childrenCalls;
    private int getCalls;
    private int suggestCalls;
    private int enrichCalls;
    private int resolveCalls;
    private int readCalls;

    /// <summary>Gets or sets the answer to describe.</summary>
    public Func<PluginId, Outcome<PluginInfo>> OnDescribe { get; set; } = _ => Outcome.Success(TestData.Info());

    /// <summary>Gets or sets the answer to search.</summary>
    public Func<PluginId, Query, Outcome<PluginNodePage>> OnSearch { get; set; } = (_, _) => Unsupported<PluginNodePage>();

    /// <summary>Gets or sets the answer to children.</summary>
    public Func<PluginId, LocalNodeId?, PageRequest, Outcome<PluginNodePage>> OnChildren { get; set; } = (_, _, _) => Unsupported<PluginNodePage>();

    /// <summary>Gets or sets the answer to get.</summary>
    public Func<PluginId, IReadOnlyList<LocalNodeId>, Outcome<IReadOnlyList<PluginNode>>> OnGet { get; set; } = (_, _) => Unsupported<IReadOnlyList<PluginNode>>();

    /// <summary>Gets or sets the answer to suggest.</summary>
    public Func<PluginId, string, string, int, Outcome<IReadOnlyList<Choice>>> OnSuggest { get; set; } = (_, _, _, _) => Unsupported<IReadOnlyList<Choice>>();

    /// <summary>Gets or sets the answer to enrich.</summary>
    public Func<PluginId, IReadOnlyList<LocalNodeId>, Outcome<IReadOnlyList<PluginNode>>> OnEnrich { get; set; } = (_, _) => Unsupported<IReadOnlyList<PluginNode>>();

    /// <summary>Gets or sets the answer to resolve.</summary>
    public Func<PluginId, ResolveRequest, Outcome<IReadOnlyList<Asset>>> OnResolve { get; set; } = (_, _) => Unsupported<IReadOnlyList<Asset>>();

    /// <summary>Gets or sets the answer to read.</summary>
    public Func<PluginId, string, ByteRange?, Outcome<Chunk>> OnRead { get; set; } = (_, _, _) => Unsupported<Chunk>();

    /// <summary>Gets how many describe calls arrived.</summary>
    public int DescribeCalls => this.describeCalls;

    /// <summary>Gets how many search calls arrived.</summary>
    public int SearchCalls => this.searchCalls;

    /// <summary>Gets how many children calls arrived.</summary>
    public int ChildrenCalls => this.childrenCalls;

    /// <summary>Gets how many get calls arrived.</summary>
    public int GetCalls => this.getCalls;

    /// <summary>Gets how many suggest calls arrived.</summary>
    public int SuggestCalls => this.suggestCalls;

    /// <summary>Gets how many enrich calls arrived.</summary>
    public int EnrichCalls => this.enrichCalls;

    /// <summary>Gets how many resolve calls arrived.</summary>
    public int ResolveCalls => this.resolveCalls;

    /// <summary>Gets how many read calls arrived.</summary>
    public int ReadCalls => this.readCalls;

    /// <inheritdoc/>
    public Task<Outcome<PluginInfo>> DescribeAsync(PluginId plugin, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref this.describeCalls);
        return Task.FromResult(this.OnDescribe(plugin));
    }

    /// <inheritdoc/>
    public Task<Outcome<PluginNodePage>> SearchAsync(PluginId plugin, Query query, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref this.searchCalls);
        return Task.FromResult(this.OnSearch(plugin, query));
    }

    /// <inheritdoc/>
    public Task<Outcome<PluginNodePage>> ChildrenAsync(
        PluginId plugin,
        LocalNodeId? parent,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref this.childrenCalls);
        return Task.FromResult(this.OnChildren(plugin, parent, request));
    }

    /// <inheritdoc/>
    public Task<Outcome<IReadOnlyList<PluginNode>>> GetAsync(
        PluginId plugin,
        IReadOnlyList<LocalNodeId> ids,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref this.getCalls);
        return Task.FromResult(this.OnGet(plugin, ids));
    }

    /// <inheritdoc/>
    public Task<Outcome<IReadOnlyList<Choice>>> SuggestAsync(
        PluginId plugin,
        string filterId,
        string text,
        int limit,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref this.suggestCalls);
        return Task.FromResult(this.OnSuggest(plugin, filterId, text, limit));
    }

    /// <inheritdoc/>
    public Task<Outcome<IReadOnlyList<PluginNode>>> EnrichAsync(
        PluginId plugin,
        IReadOnlyList<LocalNodeId> ids,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref this.enrichCalls);
        return Task.FromResult(this.OnEnrich(plugin, ids));
    }

    /// <inheritdoc/>
    public Task<Outcome<IReadOnlyList<Asset>>> ResolveAsync(
        PluginId plugin,
        ResolveRequest request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref this.resolveCalls);
        return Task.FromResult(this.OnResolve(plugin, request));
    }

    /// <inheritdoc/>
    public Task<Outcome<Chunk>> ReadAsync(
        PluginId plugin,
        string token,
        ByteRange? range,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref this.readCalls);
        return Task.FromResult(this.OnRead(plugin, token, range));
    }

    private static Outcome<T> Unsupported<T>() => Outcome.Failure<T>(ErrorCode.Unsupported, "not scripted");
}
