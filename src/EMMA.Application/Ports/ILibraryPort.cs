using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// A user's saved nodes, kept in named libraries. A node is in at most one library per user, so a node is saved or
/// not, and "move" changes which library holds it. Every user has the default library
/// (<see cref="LibraryName.Default"/>), which exists from the first time it is needed and cannot be deleted. Names
/// compare without regard to case.
/// </summary>
public interface ILibraryPort
{
    /// <summary>Adds a node to a library of the entry's user; a node that is already saved, in any library, is left where it is.</summary>
    /// <param name="entry">The entry. Its library must exist, unless it is the default library, which is created on demand.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the node was added, <see langword="false"/> when it was already saved or the library does not exist.</returns>
    Task<bool> AddAsync(LibraryEntry entry, CancellationToken cancellationToken);

    /// <summary>Removes a node from whichever library holds it.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="node">The node.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when an entry was removed.</returns>
    Task<bool> RemoveAsync(UserId user, NodeRef node, CancellationToken cancellationToken);

    /// <summary>Lists a user's saved nodes, most recently added first.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="library">One library, or <see langword="null"/> for every library.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The entries, each with the library it is in.</returns>
    Task<IReadOnlyList<LibraryEntry>> ListAsync(UserId user, LibraryName? library, CancellationToken cancellationToken);

    /// <summary>
    /// Finds saved nodes whose title contains some text, from the node cache and without asking any plugin. A node
    /// the cache no longer holds has no title to match, so it is not found.
    /// </summary>
    /// <param name="user">The owner.</param>
    /// <param name="text">The text to look for; case is ignored for ASCII letters only (SQLite's rule).</param>
    /// <param name="library">One library, or <see langword="null"/> for every library.</param>
    /// <param name="limit">The most entries to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The matching entries, most recently added first, each with the library it is in.</returns>
    Task<IReadOnlyList<LibraryEntry>> SearchAsync(UserId user, string text, LibraryName? library, int limit, CancellationToken cancellationToken);

    /// <summary>Lists a user's libraries, the default one first and the others by name. The default library is created if it does not exist yet.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Each library with how many nodes it holds.</returns>
    Task<IReadOnlyList<LibrarySummary>> ListLibrariesAsync(UserId user, CancellationToken cancellationToken);

    /// <summary>Creates an empty library.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="name">The name.</param>
    /// <param name="createdAt">When it is created.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when it was created, <see langword="false"/> when the user already has a library by that name.</returns>
    Task<bool> CreateLibraryAsync(UserId user, LibraryName name, DateTimeOffset createdAt, CancellationToken cancellationToken);

    /// <summary>Deletes a library and the entries in it. Progress, downloads and the nodes themselves are not touched. The caller decides whether the default library may go.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="name">The library.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the library existed.</returns>
    Task<bool> DeleteLibraryAsync(UserId user, LibraryName name, CancellationToken cancellationToken);

    /// <summary>Moves a saved node to another library, keeping the date it was added.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="node">The node.</param>
    /// <param name="target">The library to move it to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the node is saved and the library exists; <see langword="false"/> when either is missing.</returns>
    Task<bool> MoveAsync(UserId user, NodeRef node, LibraryName target, CancellationToken cancellationToken);
}
