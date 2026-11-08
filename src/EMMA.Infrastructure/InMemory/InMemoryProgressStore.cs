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
