using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// Remembers the complete, ordered list of a node's children as the host last listed it, so reading them again needs no
/// plugin call. Only complete listings are stored; the children themselves are kept by <see cref="INodeStorePort"/>.
/// </summary>
public interface INodeListingPort
{
    /// <summary>Reads part of a stored listing.</summary>
    /// <param name="parent">The parent node.</param>
    /// <param name="offset">How many children to skip.</param>
    /// <param name="limit">The most children to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The children's ids in order and the listing's total size, or <see langword="null"/> when the host has no complete listing.</returns>
    Task<StoredListing?> GetPageAsync(NodeRef parent, int offset, int limit, CancellationToken cancellationToken);

    /// <summary>Stores a complete listing, replacing any earlier one in one transaction.</summary>
    /// <param name="parent">The parent node.</param>
    /// <param name="children">Every child's id, in the plugin's order.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the listing is stored.</returns>
    Task ReplaceAsync(NodeRef parent, IReadOnlyList<LocalNodeId> children, CancellationToken cancellationToken);

    /// <summary>Forgets every stored listing, so the next read of each asks the plugin again.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the listings are gone.</returns>
    Task ClearAsync(CancellationToken cancellationToken);
}
