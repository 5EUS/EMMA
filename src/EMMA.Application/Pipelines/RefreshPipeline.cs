using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// Keeps what the host knows about saved nodes current: finds the children that appeared since the host last
/// listed them, and re-reads a node's own details. It reads through <see cref="CatalogPipeline"/>, so every answer is
/// validated and remembered like any other listing. Nothing here knows about any media type: "new chapters" is just
/// "children the host had not stored".
/// </summary>
/// <param name="library">The user's libraries.</param>
/// <param name="nodes">The nodes the host has seen.</param>
/// <param name="catalog">Asks plugins.</param>
/// <param name="assets">The cached asset bytes; a refreshed node's are dropped.</param>
/// <param name="resolutions">The remembered resolutions; a refreshed node's are dropped.</param>
public sealed class RefreshPipeline(LibraryPipeline library, INodeStorePort nodes, CatalogPipeline catalog, IAssetCachePort assets, IResolutionCachePort resolutions)
{
    /// <summary>
    /// Lists the children of every saved node again and reports which are new. A node whose children the host
    /// had never listed has no baseline to compare with, so it is checked but not reported (its children are
    /// stored, and the next refresh compares against them).
    /// </summary>
    /// <param name="user">The owner.</param>
    /// <param name="name">One library, or <see langword="null"/> for every library.</param>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    /// <returns>What was found, or why the library was refused.</returns>
    public async Task<Outcome<RefreshReport>> RefreshLibraryAsync(UserId user, string? name, CancellationToken cancellationToken)
    {
        Outcome<IReadOnlyList<LibraryEntry>> entries = await library.ListAsync(user, name, cancellationToken).ConfigureAwait(false);
        if (!entries.IsSuccess)
        {
            return Outcome.Failure<RefreshReport>(entries.Error);
        }

        List<NodeUpdate> updated = [];
        List<NodeRefreshFailure> failed = [];
        foreach (IGrouping<PluginId, NodeRef> group in entries.Value.Select(entry => entry.Node).GroupBy(node => node.Plugin))
        {
            // One description per plugin: a plugin that is gone or broken fails all its nodes at once, not once each.
            Outcome<PluginInfo> info = await catalog.DescribeAsync(group.Key, cancellationToken).ConfigureAwait(false);
            foreach (NodeRef node in group)
            {
                if (!info.IsSuccess)
                {
                    failed.Add(new NodeRefreshFailure(node, info.Error));
                    continue;
                }

                Outcome<NodeUpdate?> result = await this.RefreshChildrenAsync(node, info.Value, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    failed.Add(new NodeRefreshFailure(node, result.Error));
                }
                else if (result.Value is { } update)
                {
                    updated.Add(update);
                }
            }
        }

        return Outcome.Success(new RefreshReport(entries.Value.Count, updated, failed));
    }

    /// <summary>
    /// Asks the plugin for a node again, which replaces what the host stored, including its thumbnail address, and drops
    /// the node's cached asset bytes (such as its thumbnail) and remembered resolutions so they are fetched fresh.
    /// </summary>
    /// <param name="node">The node; the host must have seen it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The node as the plugin now describes it, or why it could not be had.</returns>
    public async Task<Outcome<Node>> RefreshNodeAsync(NodeRef node, CancellationToken cancellationToken)
    {
        if (await nodes.GetAsync(node, cancellationToken).ConfigureAwait(false) is null)
        {
            return Outcome.Failure<Node>(ErrorCode.NotFound, "That node has not been seen; browse or search to it first.");
        }

        Outcome<IReadOnlyList<Node>> found = await catalog.GetAsync([node], CacheMode.Refresh, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return Outcome.Failure<Node>(found.Error);
        }

        await assets.RemoveNodeAsync(node, cancellationToken).ConfigureAwait(false);
        await resolutions.RemoveNodeAsync(node, cancellationToken).ConfigureAwait(false);

        return found.Value.Count == 1
            ? Outcome.Success(found.Value[0])
            : Outcome.Failure<Node>(ErrorCode.NotFound, "The plugin no longer has that node.");
    }

    private async Task<Outcome<NodeUpdate?>> RefreshChildrenAsync(NodeRef node, PluginInfo info, CancellationToken cancellationToken)
    {
        Node? stored = await nodes.GetAsync(node, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return Outcome.Failure<NodeUpdate?>(ErrorCode.NotFound, "The host no longer knows that node; open it again.");
        }

        // A kind the plugin declares without children (a chapter, a track) is a leaf: there is nothing to list.
        if (info.FindKind(stored.Kind) is not { Children.Count: > 0 })
        {
            return Outcome.Success<NodeUpdate?>(null);
        }

        // Read the baseline first: listing the children stores them, after which every child would look known.
        IReadOnlySet<LocalNodeId> known = await nodes.ChildIdsAsync(node, cancellationToken).ConfigureAwait(false);
        Outcome<IReadOnlyList<Node>> children = await catalog.RelistChildrenAsync(node, cancellationToken).ConfigureAwait(false);
        if (!children.IsSuccess)
        {
            return Outcome.Failure<NodeUpdate?>(children.Error);
        }

        List<Node> added = known.Count == 0 ? [] : [.. children.Value.Where(child => !known.Contains(child.Ref.Id))];
        return Outcome.Success<NodeUpdate?>(added.Count == 0 ? null : new NodeUpdate(stored, added));
    }
}
