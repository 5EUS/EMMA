using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// Finds what a download job stores: the leaves beneath a node. A leaf is a node whose kind declares the job's
/// role, so the walk never asks what a kind means (a chapter and a season are both just a node with children, a
/// page and an episode are both just a node with an asset role).
/// </summary>
/// <param name="catalog">Lists children and describes plugins.</param>
/// <param name="nodes">Remembers the nodes the host has served, which is where a starting node's kind comes from.</param>
internal sealed class DownloadWalker(CatalogPipeline catalog, INodeStorePort nodes)
{
    /// <summary>The most leaves one job may have. A series of ten thousand pages is a mistake, not a download.</summary>
    public const int MaxLeaves = 5000;

    private const int MaxDepth = 8;
    private const int MaxPagesPerContainer = 200;

    /// <summary>Walks down from a node, in the order the plugin lists children.</summary>
    /// <param name="start">The node a job was created for.</param>
    /// <param name="role">The asset role the leaves must declare.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The leaves in order, or why the walk failed.</returns>
    public async Task<Outcome<IReadOnlyList<NodeRef>>> WalkAsync(NodeRef start, AssetRole role, CancellationToken cancellationToken)
    {
        Node? known = await nodes.GetAsync(start, cancellationToken).ConfigureAwait(false);
        if (known is null)
        {
            return Outcome.Failure<IReadOnlyList<NodeRef>>(ErrorCode.NotFound, "The host no longer knows that node; open it again before downloading.");
        }

        Outcome<PluginInfo> info = await catalog.DescribeAsync(start.Plugin, cancellationToken).ConfigureAwait(false);
        if (!info.IsSuccess)
        {
            return Outcome.Failure<IReadOnlyList<NodeRef>>(info.Error);
        }

        List<NodeRef> leaves = [];
        PluginError? failure = await this.VisitAsync(start, known.Kind, info.Value, role, depth: 0, leaves, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return Outcome.Failure<IReadOnlyList<NodeRef>>(failure);
        }

        return leaves.Count == 0
            ? Outcome.Failure<IReadOnlyList<NodeRef>>(ErrorCode.Unsupported, $"Nothing beneath that node has '{role}' assets to download.")
            : Outcome.Success<IReadOnlyList<NodeRef>>(leaves);
    }

    private static PluginError TooMany() => new(ErrorCode.Unsupported, $"That node has more than {MaxLeaves} items to download.");

    private async Task<PluginError?> VisitAsync(
        NodeRef node,
        NodeKind kind,
        PluginInfo info,
        AssetRole role,
        int depth,
        List<NodeRef> leaves,
        CancellationToken cancellationToken)
    {
        KindInfo? declared = info.FindKind(kind);
        if (declared is null)
        {
            return null;
        }

        if (declared.Roles.Contains(role))
        {
            if (leaves.Count >= MaxLeaves)
            {
                return TooMany();
            }

            leaves.Add(node);
            return null;
        }

        if (declared.Children.Count == 0)
        {
            return null;
        }

        if (depth >= MaxDepth)
        {
            return new PluginError(ErrorCode.Unsupported, $"That node is nested more than {MaxDepth} levels deep.");
        }

        string? cursor = null;
        HashSet<string> seen = [];
        for (int page = 0; page < MaxPagesPerContainer; page++)
        {
            Outcome<NodePage> children = await catalog.ChildrenAsync(node, new PageRequest(cursor, ContractLimits.MaxNodesPerPage), cancellationToken).ConfigureAwait(false);
            if (!children.IsSuccess)
            {
                return children.Error;
            }

            foreach (Node child in children.Value.Items)
            {
                PluginError? failure = await this.VisitAsync(child.Ref, child.Kind, info, role, depth + 1, leaves, cancellationToken).ConfigureAwait(false);
                if (failure is not null)
                {
                    return failure;
                }
            }

            cursor = children.Value.NextCursor;
            if (cursor is null)
            {
                return null;
            }

            if (!seen.Add(cursor))
            {
                return new PluginError(ErrorCode.Internal, $"Plugin {node.Plugin} repeated a paging cursor while listing the children of '{node.Id}'.");
            }
        }

        return new PluginError(ErrorCode.Unsupported, $"'{node.Id}' has more than {MaxPagesPerContainer} pages of children.");
    }
}
