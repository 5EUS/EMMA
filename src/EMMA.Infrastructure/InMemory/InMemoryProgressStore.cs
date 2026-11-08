using System.Collections.Concurrent;

using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.InMemory;

/// <summary>
/// A progress store that forgets everything on exit. For tests and development; SQLite
/// is the real store. "Latest within" walks parent links through the node store.
/// </summary>
/// <param name="nodes">The node store used to follow parent links.</param>
public sealed class InMemoryProgressStore(INodeStorePort nodes) : IProgressPort
{
    private const int MaxDepth = 32;

    private readonly ConcurrentDictionary<(UserId User, NodeRef Node), Progress> entries = new();

    /// <inheritdoc/>
    public Task UpsertAsync(Progress progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        this.entries[(progress.User, progress.Node)] = progress;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<Progress?> GetAsync(UserId user, NodeRef node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.entries.GetValueOrDefault((user, node)));
    }

    /// <inheritdoc/>
    public async Task<Progress?> GetLatestWithinAsync(UserId user, NodeRef ancestor, CancellationToken cancellationToken)
    {
        Progress? latest = null;
        foreach (Progress candidate in this.entries.Values.Where(entry => entry.User == user))
        {
            if ((latest is null || candidate.UpdatedAt > latest.UpdatedAt)
                && await this.IsWithinAsync(candidate.Node, ancestor, cancellationToken).ConfigureAwait(false))
            {
                latest = candidate;
            }
        }

        return latest;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Progress>> ListRecentAsync(UserId user, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Progress> recent =
        [
            .. this.entries.Values
                .Where(entry => entry.User == user)
                .OrderByDescending(entry => entry.UpdatedAt)
                .Take(Math.Max(0, limit)),
        ];
        return Task.FromResult(recent);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Progress>> ListWithinAsync(UserId user, NodeRef ancestor, int limit, CancellationToken cancellationToken)
    {
        List<Progress> within = [];
        foreach (Progress candidate in this.entries.Values.Where(entry => entry.User == user))
        {
            if (await this.IsWithinAsync(candidate.Node, ancestor, cancellationToken).ConfigureAwait(false))
            {
                within.Add(candidate);
            }
        }

        return [.. within.OrderByDescending(entry => entry.UpdatedAt).ThenBy(entry => entry.Node.Id.Value, StringComparer.Ordinal).Take(Math.Max(0, limit))];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProgressGroup>> ListGroupsAsync(UserId user, int limit, CancellationToken cancellationToken)
    {
        Dictionary<NodeRef, List<Progress>> groups = [];
        foreach (Progress entry in this.entries.Values.Where(entry => entry.User == user))
        {
            NodeRef root = await this.RootOfAsync(entry.Node, cancellationToken).ConfigureAwait(false);
            if (!groups.TryGetValue(root, out List<Progress>? members))
            {
                groups[root] = members = [];
            }

            members.Add(entry);
        }

        return
        [
            .. groups
                .Select(group =>
                {
                    Progress latest = group.Value.OrderByDescending(entry => entry.UpdatedAt).ThenBy(entry => entry.Node.Id.Value, StringComparer.Ordinal).First();
                    int completed = group.Value.Count(entry => entry.Completed);
                    return new ProgressGroup(group.Key, latest, completed, group.Value.Count - completed);
                })
                .OrderByDescending(group => group.Latest.UpdatedAt)
                .ThenBy(group => group.Series.Id.Value, StringComparer.Ordinal)
                .Take(Math.Max(0, limit)),
        ];
    }

    /// <inheritdoc/>
    public async Task<int> DeleteWithinAsync(UserId user, NodeRef root, CancellationToken cancellationToken)
    {
        int removed = 0;
        foreach (Progress candidate in this.entries.Values.Where(entry => entry.User == user))
        {
            if (await this.IsWithinAsync(candidate.Node, root, cancellationToken).ConfigureAwait(false)
                && this.entries.TryRemove((candidate.User, candidate.Node), out _))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <inheritdoc/>
    public Task<int> ClearAsync(UserId user, CancellationToken cancellationToken) =>
        this.RemoveWhere(entry => entry.User == user, cancellationToken);

    /// <inheritdoc/>
    public Task<int> DeleteBeforeAsync(UserId user, DateTimeOffset before, CancellationToken cancellationToken) =>
        this.RemoveWhere(entry => entry.User == user && entry.UpdatedAt < before, cancellationToken);

    private Task<int> RemoveWhere(Func<Progress, bool> match, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int removed = 0;
        foreach (Progress entry in this.entries.Values.Where(match))
        {
            if (this.entries.TryRemove((entry.User, entry.Node), out _))
            {
                removed++;
            }
        }

        return Task.FromResult(removed);
    }

    // The topmost node reachable through cached parents; a node that is not cached is its own root.
    private async Task<NodeRef> RootOfAsync(NodeRef node, CancellationToken cancellationToken)
    {
        NodeRef current = node;
        for (int depth = 0; depth < MaxDepth; depth++)
        {
            Node? stored = await nodes.GetAsync(current, cancellationToken).ConfigureAwait(false);
            if (stored?.Parent is not { } parent || await nodes.GetAsync(parent, cancellationToken).ConfigureAwait(false) is null)
            {
                return current;
            }

            current = parent;
        }

        return current;
    }

    private async Task<bool> IsWithinAsync(NodeRef node, NodeRef ancestor, CancellationToken cancellationToken)
    {
        NodeRef current = node;
        for (int depth = 0; depth <= MaxDepth; depth++)
        {
            if (current == ancestor)
            {
                return true;
            }

            Node? stored = await nodes.GetAsync(current, cancellationToken).ConfigureAwait(false);
            if (stored?.Parent is not { } parent)
            {
                return false;
            }

            current = parent;
        }

        return false;
    }
}
