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
/// <param name="listings">Where complete child listings are remembered.</param>
/// <param name="executor">Runs plugin calls with retries and timeouts.</param>
public sealed class CatalogPipeline(IPluginGateway gateway, INodeStorePort store, INodeListingPort listings, PluginCallExecutor executor)
{
    private const int MaxPagesPerListing = 200;
    private const string CursorPrefix = "emma:";

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

    /// <summary>Suggests values for one of a plugin's lookup filters from partly typed text.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="filterId">The id of a filter the plugin declares with the lookup type.</param>
    /// <param name="text">The partial text. Text shorter than the filter's minimum query length yields no suggestions.</param>
    /// <param name="limit">The most suggestions wanted; capped at the filter's maximum.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The suggestions, or why they could not be had; <see cref="ErrorCode.Unsupported"/> when the plugin does not assist.</returns>
    public async Task<Outcome<IReadOnlyList<Choice>>> SuggestAsync(
        PluginId plugin,
        string filterId,
        string text,
        int limit,
        CancellationToken cancellationToken)
    {
        Outcome<PluginInfo> info = await this.DescribeAsync(plugin, cancellationToken).ConfigureAwait(false);
        if (!info.IsSuccess)
        {
            return Outcome.Failure<IReadOnlyList<Choice>>(info.Error);
        }

        FilterSpec? spec = info.Value.Filters.FirstOrDefault(filter => string.Equals(filter.Id, filterId, StringComparison.Ordinal));
        if (spec is not { Type: FilterType.Lookup, Lookup: { } lookup })
        {
            return Outcome.Failure<IReadOnlyList<Choice>>(ErrorCode.InvalidArgument, $"The plugin has no lookup filter '{filterId}'.");
        }

        if (text.Length > ContractLimits.MaxQueryTextLength)
        {
            return Outcome.Failure<IReadOnlyList<Choice>>(ErrorCode.InvalidArgument, $"Suggestion text is longer than {ContractLimits.MaxQueryTextLength} characters.");
        }

        if (limit < 1)
        {
            return Outcome.Failure<IReadOnlyList<Choice>>(ErrorCode.InvalidArgument, "The limit must be at least 1.");
        }

        if (text.Length < lookup.MinQueryLength)
        {
            return Outcome.Success<IReadOnlyList<Choice>>([]);
        }

        int wanted = lookup.MaxResults > 0 ? Math.Min(limit, lookup.MaxResults) : limit;
        Outcome<IReadOnlyList<Choice>> raw = await executor
            .ExecuteAsync(token => gateway.SuggestAsync(plugin, filterId, text, wanted, token), cancellationToken)
            .ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return raw;
        }

        if (raw.Value.Any(choice => string.IsNullOrWhiteSpace(choice.Value) || string.IsNullOrWhiteSpace(choice.Label)))
        {
            return Outcome.Failure<IReadOnlyList<Choice>>(ErrorCode.Internal, $"Plugin {plugin} returned a suggestion with a blank value or label.");
        }

        return Outcome.Success<IReadOnlyList<Choice>>([.. raw.Value.Take(wanted)]);
    }

    /// <summary>
    /// Fills in expensive props (ratings, counts) of nodes the host has already seen, typically search results,
    /// and remembers the merged result. The plugin's props replace the stored props with the same key; every other
    /// stored prop, and the title and thumbnail, stay as they were. Nodes the host has not seen are omitted.
    /// </summary>
    /// <param name="references">The nodes to enrich; they may belong to different plugins.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The merged nodes in the order requested, or why a plugin failed; <see cref="ErrorCode.Unsupported"/> when a plugin does not assist.</returns>
    public async Task<Outcome<IReadOnlyList<Node>>> EnrichAsync(IReadOnlyList<NodeRef> references, CancellationToken cancellationToken)
    {
        if (references.Count > ContractLimits.MaxIdsPerGet)
        {
            return Outcome.Failure<IReadOnlyList<Node>>(
                ErrorCode.InvalidArgument,
                $"At most {ContractLimits.MaxIdsPerGet} nodes can be enriched at once.");
        }

        Dictionary<NodeRef, Node> merged = [];
        foreach (IGrouping<PluginId, NodeRef> group in references.GroupBy(reference => reference.Plugin))
        {
            Outcome<IReadOnlyList<Node>> nodes = await this.EnrichFromPluginAsync(group.Key, [.. group.Distinct()], cancellationToken).ConfigureAwait(false);
            if (!nodes.IsSuccess)
            {
                return nodes;
            }

            foreach (Node node in nodes.Value)
            {
                merged[node.Ref] = node;
            }
        }

        return Outcome.Success<IReadOnlyList<Node>>(
            [.. references.Where(merged.ContainsKey).Select(reference => merged[reference])]);
    }

    /// <summary>Lists a plugin's browse root: its landing sections or top-level content.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="request">Cursor and limit.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A page of nodes, or why listing failed.</returns>
    public Task<Outcome<NodePage>> BrowseRootAsync(PluginId plugin, PageRequest request, CancellationToken cancellationToken) =>
        this.ListAsync(plugin, parent: null, request, cancellationToken);

    /// <summary>
    /// Lists the children of a node in natural order: chapters, pages, episodes, tracks. For a node whose kind declares
    /// children the host lists them completely once and then serves every page from that stored listing, with cursors
    /// of its own; <see cref="CacheMode.Refresh"/> lists them again. A kind without children is asked live.
    /// </summary>
    /// <param name="parent">The parent node.</param>
    /// <param name="request">Cursor and limit.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A page of nodes, or why listing failed.</returns>
    public Task<Outcome<NodePage>> ChildrenAsync(NodeRef parent, PageRequest request, CancellationToken cancellationToken) =>
        this.ChildrenAsync(parent, request, CacheMode.Use, cancellationToken);

    /// <summary>Lists the children of a node, choosing whether a stored listing may answer; see the overload without a mode.</summary>
    /// <param name="parent">The parent node.</param>
    /// <param name="request">Cursor and limit.</param>
    /// <param name="mode">Whether a stored listing may be used.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A page of nodes, or why listing failed.</returns>
    public async Task<Outcome<NodePage>> ChildrenAsync(NodeRef parent, PageRequest request, CacheMode mode, CancellationToken cancellationToken)
    {
        PluginError? invalid = QueryValidator.ValidatePaging(request.Cursor, request.Limit);
        if (invalid is not null)
        {
            return Outcome.Failure<NodePage>(invalid);
        }

        bool ours = TryReadOffset(request.Cursor, out int offset);
        if (request.Cursor is not null && !ours)
        {
            // A cursor the host did not make belongs to the plugin; answer it live.
            return await this.ListAsync(parent.Plugin, parent.Id, request, cancellationToken).ConfigureAwait(false);
        }

        Node? stored = await store.GetAsync(parent, cancellationToken).ConfigureAwait(false);
        Outcome<PluginInfo> info = await this.DescribeAsync(parent.Plugin, cancellationToken).ConfigureAwait(false);
        if (!info.IsSuccess)
        {
            return Outcome.Failure<NodePage>(info.Error);
        }

        if (stored is null || info.Value.FindKind(stored.Kind) is not { Children.Count: > 0 })
        {
            return await this.ListAsync(parent.Plugin, parent.Id, request, cancellationToken).ConfigureAwait(false);
        }

        int limit = request.Limit ?? ContractLimits.MaxNodesPerPage;
        if (mode == CacheMode.Use && await this.ReadStoredPageAsync(parent, offset, limit, cancellationToken).ConfigureAwait(false) is { } hit)
        {
            return Outcome.Success(hit);
        }

        Outcome<IReadOnlyList<Node>> all = await this.RelistChildrenAsync(parent, cancellationToken).ConfigureAwait(false);
        return all.IsSuccess
            ? Outcome.Success(SlicePage(all.Value, offset, limit))
            : Outcome.Failure<NodePage>(all.Error);
    }

    /// <summary>
    /// Asks the plugin for every child of a node, page by page, and replaces the host's stored listing with the answer.
    /// Each page is validated and remembered like any other listing. Children the plugin no longer lists leave the
    /// listing but stay in the node store, because progress and libraries may still refer to them.
    /// </summary>
    /// <param name="parent">The parent node.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Every child in the plugin's order, or why the listing could not be completed (nothing is replaced then).</returns>
    public async Task<Outcome<IReadOnlyList<Node>>> RelistChildrenAsync(NodeRef parent, CancellationToken cancellationToken)
    {
        List<Node> all = [];
        HashSet<string> seen = [];
        string? cursor = null;
        for (int page = 0; page < MaxPagesPerListing; page++)
        {
            Outcome<NodePage> children = await this.ListAsync(parent.Plugin, parent.Id, new PageRequest(cursor, ContractLimits.MaxNodesPerPage), cancellationToken).ConfigureAwait(false);
            if (!children.IsSuccess)
            {
                return Outcome.Failure<IReadOnlyList<Node>>(children.Error);
            }

            all.AddRange(children.Value.Items);
            cursor = children.Value.NextCursor;
            if (cursor is null)
            {
                await listings.ReplaceAsync(parent, [.. all.Select(child => child.Ref.Id)], cancellationToken).ConfigureAwait(false);
                return Outcome.Success<IReadOnlyList<Node>>(all);
            }

            if (!seen.Add(cursor))
            {
                return Outcome.Failure<IReadOnlyList<Node>>(ErrorCode.Internal, $"Plugin {parent.Plugin} repeated a paging cursor while listing the children of '{parent.Id}'.");
            }
        }

        return Outcome.Failure<IReadOnlyList<Node>>(ErrorCode.Unsupported, $"'{parent.Id}' has more than {MaxPagesPerListing} pages of children.");
    }

    /// <summary>
    /// Fetches nodes by reference, in the order requested. Unknown nodes are omitted. A node the host already holds in
    /// full is answered from its store; only the others are asked of the plugin.
    /// </summary>
    /// <param name="references">The nodes to fetch; they may belong to different plugins.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The nodes found, or why a plugin failed.</returns>
    public Task<Outcome<IReadOnlyList<Node>>> GetAsync(IReadOnlyList<NodeRef> references, CancellationToken cancellationToken) =>
        this.GetAsync(references, CacheMode.Use, cancellationToken);

    /// <summary>Fetches nodes by reference, choosing whether stored nodes may answer; see the overload without a mode.</summary>
    /// <param name="references">The nodes to fetch; they may belong to different plugins.</param>
    /// <param name="mode">Whether stored nodes may be used.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The nodes found, or why a plugin failed.</returns>
    public async Task<Outcome<IReadOnlyList<Node>>> GetAsync(IReadOnlyList<NodeRef> references, CacheMode mode, CancellationToken cancellationToken)
    {
        if (references.Count > ContractLimits.MaxIdsPerGet)
        {
            return Outcome.Failure<IReadOnlyList<Node>>(
                ErrorCode.InvalidArgument,
                $"At most {ContractLimits.MaxIdsPerGet} nodes can be fetched at once.");
        }

        List<NodeRef> distinct = [.. references.Distinct()];
        Dictionary<NodeRef, Node> found = [];
        if (mode == CacheMode.Use)
        {
            foreach (Node node in await store.GetManyAsync(distinct, detailedOnly: true, cancellationToken).ConfigureAwait(false))
            {
                found[node.Ref] = node;
            }
        }

        foreach (IGrouping<PluginId, NodeRef> group in distinct.Where(reference => !found.ContainsKey(reference)).GroupBy(reference => reference.Plugin))
        {
            Outcome<IReadOnlyList<Node>> nodes = await this.GetFromPluginAsync(group.Key, [.. group], cancellationToken).ConfigureAwait(false);
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

    private static bool TryReadOffset(string? cursor, out int offset)
    {
        offset = 0;
        return cursor is null
            || (cursor.StartsWith(CursorPrefix, StringComparison.Ordinal)
                && int.TryParse(cursor.AsSpan(CursorPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out offset));
    }

    private static NodePage SlicePage(IReadOnlyList<Node> all, int offset, int limit)
    {
        List<Node> items = [.. all.Skip(offset).Take(limit)];
        int next = offset + items.Count;
        return new NodePage(items, next < all.Count ? $"{CursorPrefix}{next}" : null, all.Count, []);
    }

    private static Node MergeProps(Node original, Node enriched)
    {
        HashSet<string> replaced = [.. enriched.Props.Select(prop => prop.Key)];
        return original with { Props = [.. original.Props.Where(prop => !replaced.Contains(prop.Key)), .. enriched.Props] };
    }

    private async Task<Outcome<IReadOnlyList<Node>>> EnrichFromPluginAsync(
        PluginId plugin,
        IReadOnlyList<NodeRef> requested,
        CancellationToken cancellationToken)
    {
        Dictionary<NodeRef, Node> originals = [];
        foreach (NodeRef reference in requested)
        {
            Node? seen = await store.GetAsync(reference, cancellationToken).ConfigureAwait(false);
            if (seen is not null)
            {
                originals[reference] = seen;
            }
        }

        if (originals.Count == 0)
        {
            return Outcome.Success<IReadOnlyList<Node>>([]);
        }

        Outcome<PluginInfo> info = await this.DescribeAsync(plugin, cancellationToken).ConfigureAwait(false);
        if (!info.IsSuccess)
        {
            return Outcome.Failure<IReadOnlyList<Node>>(info.Error);
        }

        List<LocalNodeId> ids = [.. originals.Keys.Select(reference => reference.Id)];
        Outcome<IReadOnlyList<PluginNode>> raw = await executor
            .ExecuteAsync(token => gateway.EnrichAsync(plugin, ids, token), cancellationToken)
            .ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return Outcome.Failure<IReadOnlyList<Node>>(raw.Error);
        }

        Outcome<IReadOnlyList<Node>> enriched = NodeValidator.ValidateNodes(plugin, info.Value, contextParent: null, raw.Value);
        if (!enriched.IsSuccess)
        {
            return enriched;
        }

        Node? unrequested = enriched.Value.FirstOrDefault(node => !originals.ContainsKey(node.Ref));
        if (unrequested is not null)
        {
            return Outcome.Failure<IReadOnlyList<Node>>(
                ErrorCode.Internal,
                $"Plugin {plugin} returned node '{unrequested.Ref.Id}', which was not requested.");
        }

        List<Node> results = [.. enriched.Value.Select(node => MergeProps(originals[node.Ref], node))];
        await this.RememberAsync(results, cancellationToken).ConfigureAwait(false);
        return Outcome.Success<IReadOnlyList<Node>>(results);
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

        if (nodes.Value.Count > 0)
        {
            await store.UpsertDetailedAsync(nodes.Value, cancellationToken).ConfigureAwait(false);
        }

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

    private async Task<NodePage?> ReadStoredPageAsync(NodeRef parent, int offset, int limit, CancellationToken cancellationToken)
    {
        StoredListing? listing = await listings.GetPageAsync(parent, offset, limit, cancellationToken).ConfigureAwait(false);
        if (listing is null)
        {
            return null;
        }

        List<NodeRef> references = [.. listing.Children.Select(id => new NodeRef(parent.Plugin, id))];
        Dictionary<NodeRef, Node> nodes = (await store.GetManyAsync(references, detailedOnly: false, cancellationToken).ConfigureAwait(false))
            .ToDictionary(node => node.Ref);
        if (references.Exists(reference => !nodes.ContainsKey(reference)))
        {
            // The listing names a child the node store lost; list again instead of serving a short page.
            return null;
        }

        int next = offset + references.Count;
        return new NodePage([.. references.Select(reference => nodes[reference])], next < listing.Total ? $"{CursorPrefix}{next}" : null, listing.Total, []);
    }

    private Task RememberAsync(IReadOnlyList<Node> nodes, CancellationToken cancellationToken) =>
        nodes.Count == 0 ? Task.CompletedTask : store.UpsertAsync(nodes, cancellationToken);
}
