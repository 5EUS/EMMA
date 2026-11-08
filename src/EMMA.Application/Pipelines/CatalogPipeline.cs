using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// Reads the media tree: search, browsing, children and lookup by id. It validates the
/// request against the plugin's declaration, calls the plugin with retries, validates
/// the answer against the contract, namespaces ids, and remembers what it saw. Nothing
/// here knows about any media type.
/// </summary>
/// <param name="gateway">How to reach plugins.</param>
/// <param name="store">Where seen nodes are remembered.</param>
/// <param name="executor">Runs plugin calls with retries and timeouts.</param>
public sealed class CatalogPipeline(IPluginGateway gateway, INodeStorePort store, PluginCallExecutor executor)
{
    /// <summary>Searches one plugin.</summary>
    /// <param name="plugin">The plugin to search.</param>
    /// <param name="query">The search request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A page of nodes, or why the search failed.</returns>
    public async Task<Outcome<NodePage>> SearchAsync(PluginId plugin, Query query, CancellationToken cancellationToken)
    {
        Outcome<PluginInfo> info = await this.DescribeAsync(plugin, cancellationToken).ConfigureAwait(false);
        if (!info.IsSuccess)
        {
            return Outcome.Failure<NodePage>(info.Error);
        }

        PluginError? invalid = QueryValidator.ValidateQuery(info.Value, query);
        if (invalid is not null)
        {
            return Outcome.Failure<NodePage>(invalid);
        }

        Outcome<PluginNodePage> raw = await executor
            .ExecuteAsync(token => gateway.SearchAsync(plugin, query, token), cancellationToken)
            .ConfigureAwait(false);
        return await this.FinishPageAsync(plugin, info.Value, contextParent: null, raw, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists a plugin's browse root: its landing sections or top-level content.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="request">Cursor and limit.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A page of nodes, or why listing failed.</returns>
    public Task<Outcome<NodePage>> BrowseRootAsync(PluginId plugin, PageRequest request, CancellationToken cancellationToken) =>
        this.ListAsync(plugin, parent: null, request, cancellationToken);

    /// <summary>Lists the children of a node in natural order: chapters, pages, episodes, tracks.</summary>
    /// <param name="parent">The parent node.</param>
    /// <param name="request">Cursor and limit.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A page of nodes, or why listing failed.</returns>
    public Task<Outcome<NodePage>> ChildrenAsync(NodeRef parent, PageRequest request, CancellationToken cancellationToken) =>
        this.ListAsync(parent.Plugin, parent.Id, request, cancellationToken);

    /// <summary>Fetches nodes by reference, in the order requested. Unknown nodes are omitted.</summary>
    /// <param name="references">The nodes to fetch; they may belong to different plugins.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The nodes found, or why a plugin failed.</returns>
    public async Task<Outcome<IReadOnlyList<Node>>> GetAsync(IReadOnlyList<NodeRef> references, CancellationToken cancellationToken)
    {
        if (references.Count > ContractLimits.MaxIdsPerGet)
        {
            return Outcome.Failure<IReadOnlyList<Node>>(
                ErrorCode.InvalidArgument,
                $"At most {ContractLimits.MaxIdsPerGet} nodes can be fetched at once.");
        }

        Dictionary<NodeRef, Node> found = [];
        foreach (IGrouping<PluginId, NodeRef> group in references.GroupBy(reference => reference.Plugin))
        {
            Outcome<IReadOnlyList<Node>> nodes = await this.GetFromPluginAsync(group.Key, [.. group.Distinct()], cancellationToken).ConfigureAwait(false);
            if (!nodes.IsSuccess)
            {
                return nodes;
            }

            foreach (Node node in nodes.Value)
            {
                found[node.Ref] = node;
            }
        }

        return Outcome.Success<IReadOnlyList<Node>>(
            [.. references.Where(found.ContainsKey).Select(reference => found[reference])]);
    }

    /// <summary>Gets what a plugin says about itself, checked against the contract.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plugin's declaration, or why it could not be had or was rejected.</returns>
    public async Task<Outcome<PluginInfo>> DescribeAsync(PluginId plugin, CancellationToken cancellationToken)
    {
        Outcome<PluginInfo> info = await executor.ExecuteAsync(token => gateway.DescribeAsync(plugin, token), cancellationToken).ConfigureAwait(false);
        string? problem = info.IsSuccess ? PluginInfoValidator.FindProblem(info.Value) : null;
        return problem is null
            ? info
            : Outcome.Failure<PluginInfo>(ErrorCode.Internal, $"Plugin {plugin} {problem}.");
    }

    private async Task<Outcome<IReadOnlyList<Node>>> GetFromPluginAsync(
        PluginId plugin,
        IReadOnlyList<NodeRef> requested,
        CancellationToken cancellationToken)
    {
        Outcome<PluginInfo> info = await this.DescribeAsync(plugin, cancellationToken).ConfigureAwait(false);
        if (!info.IsSuccess)
        {
            return Outcome.Failure<IReadOnlyList<Node>>(info.Error);
        }

        List<LocalNodeId> ids = [.. requested.Select(reference => reference.Id)];
        Outcome<IReadOnlyList<PluginNode>> raw = await executor
            .ExecuteAsync(token => gateway.GetAsync(plugin, ids, token), cancellationToken)
            .ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return Outcome.Failure<IReadOnlyList<Node>>(raw.Error);
        }

        Outcome<IReadOnlyList<Node>> nodes = NodeValidator.ValidateNodes(plugin, info.Value, contextParent: null, raw.Value);
        if (!nodes.IsSuccess)
        {
            return nodes;
        }

        HashSet<NodeRef> asked = [.. requested];
        Node? unrequested = nodes.Value.FirstOrDefault(node => !asked.Contains(node.Ref));
        if (unrequested is not null)
        {
            return Outcome.Failure<IReadOnlyList<Node>>(
                ErrorCode.Internal,
                $"Plugin {plugin} returned node '{unrequested.Ref.Id}', which was not requested.");
        }

        await this.RememberAsync(nodes.Value, cancellationToken).ConfigureAwait(false);
        return nodes;
    }

    private async Task<Outcome<NodePage>> ListAsync(
        PluginId plugin,
        LocalNodeId? parent,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        PluginError? invalid = QueryValidator.ValidatePaging(request.Cursor, request.Limit);
        if (invalid is not null)
        {
            return Outcome.Failure<NodePage>(invalid);
        }

        Outcome<PluginInfo> info = await this.DescribeAsync(plugin, cancellationToken).ConfigureAwait(false);
        if (!info.IsSuccess)
        {
            return Outcome.Failure<NodePage>(info.Error);
        }

        Outcome<PluginNodePage> raw = await executor
            .ExecuteAsync(token => gateway.ChildrenAsync(plugin, parent, request, token), cancellationToken)
            .ConfigureAwait(false);
        return await this.FinishPageAsync(plugin, info.Value, parent, raw, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Outcome<NodePage>> FinishPageAsync(
        PluginId plugin,
        PluginInfo info,
        LocalNodeId? contextParent,
        Outcome<PluginNodePage> raw,
        CancellationToken cancellationToken)
    {
        if (!raw.IsSuccess)
        {
            return Outcome.Failure<NodePage>(raw.Error);
        }

        Outcome<NodePage> page = NodeValidator.ValidatePage(plugin, info, contextParent, raw.Value);
        if (page.IsSuccess)
        {
            await this.RememberAsync(page.Value.Items, cancellationToken).ConfigureAwait(false);
        }

        return page;
    }

    private Task RememberAsync(IReadOnlyList<Node> nodes, CancellationToken cancellationToken) =>
        nodes.Count == 0 ? Task.CompletedTask : store.UpsertAsync(nodes, cancellationToken);
}
