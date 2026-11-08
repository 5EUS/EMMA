using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// A user's saved nodes: one library per user. Named libraries are deliberately absent
/// until a client needs them (see the note in EMMA.Application.csproj).
/// </summary>
public interface ILibraryPort
{
    /// <summary>Adds a node to a user's library; adding twice keeps the original date.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the entry is stored.</returns>
    Task AddAsync(LibraryEntry entry, CancellationToken cancellationToken);

    /// <summary>Removes a node from a user's library.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="node">The node.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when an entry was removed.</returns>
    Task<bool> RemoveAsync(UserId user, NodeRef node, CancellationToken cancellationToken);

    /// <summary>Lists a user's library, most recently added first.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The entries.</returns>
    Task<IReadOnlyList<LibraryEntry>> ListAsync(UserId user, CancellationToken cancellationToken);
}
