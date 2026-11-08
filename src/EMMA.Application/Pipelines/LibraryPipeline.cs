using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// A user's libraries: which nodes are saved and where. Holds the rules the store does not, such as that only a node
/// the host has served can be saved (so a library never holds a reference nothing can resolve) and that the default
/// library stays.
/// </summary>
/// <param name="store">Where libraries are kept.</param>
/// <param name="nodes">The nodes the host has seen.</param>
/// <param name="time">The time source that stamps each addition.</param>
public sealed class LibraryPipeline(ILibraryPort store, INodeStorePort nodes, TimeProvider time)
{
    private const int MaxSearchResults = 200;
    private const string InvalidName = "A library name must be 1 to 64 characters without control characters.";
    private const string NoSuchLibrary = "You have no library with that name.";

    /// <summary>Lists a user's libraries; the default one is always there.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Each library with how many nodes it holds.</returns>
    public Task<IReadOnlyList<LibrarySummary>> LibrariesAsync(UserId user, CancellationToken cancellationToken) =>
        store.ListLibrariesAsync(user, cancellationToken);

    /// <summary>Creates a library.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="name">The name; surrounding white space is ignored.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The new library, or why it was refused.</returns>
    public async Task<Outcome<LibrarySummary>> CreateAsync(UserId user, string? name, CancellationToken cancellationToken)
    {
        if (!LibraryName.TryCreate(name, out LibraryName library))
        {
            return Outcome.Failure<LibrarySummary>(ErrorCode.InvalidArgument, InvalidName);
        }

        DateTimeOffset now = time.GetUtcNow();
        return await store.CreateLibraryAsync(user, library, now, cancellationToken).ConfigureAwait(false)
            ? Outcome.Success(new LibrarySummary(library, now, 0))
            : Outcome.Failure<LibrarySummary>(ErrorCode.InvalidArgument, "You already have a library with that name.");
    }

    /// <summary>Deletes a library and the entries in it. Progress and downloads stay.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="name">The library.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Success, or why it was refused: the default library cannot be deleted.</returns>
    public async Task<Outcome<bool>> DeleteAsync(UserId user, string? name, CancellationToken cancellationToken)
    {
        if (!LibraryName.TryCreate(name, out LibraryName library))
        {
            return Outcome.Failure<bool>(ErrorCode.InvalidArgument, InvalidName);
        }

        if (library.Matches(LibraryName.Default))
        {
            return Outcome.Failure<bool>(ErrorCode.InvalidArgument, "The default library cannot be deleted.");
        }

        return await store.DeleteLibraryAsync(user, library, cancellationToken).ConfigureAwait(false)
            ? Outcome.Success(true)
            : Outcome.Failure<bool>(ErrorCode.NotFound, NoSuchLibrary);
    }

    /// <summary>Lists saved nodes, newest first.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="name">One library, or <see langword="null"/> for every library.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The entries, each with its library, or why the library was refused.</returns>
    public async Task<Outcome<IReadOnlyList<LibraryEntry>>> ListAsync(UserId user, string? name, CancellationToken cancellationToken)
    {
        Outcome<LibraryName?> library = await this.ResolveAsync(user, name, cancellationToken).ConfigureAwait(false);
        return library.IsSuccess
            ? Outcome.Success(await store.ListAsync(user, library.Value, cancellationToken).ConfigureAwait(false))
            : Outcome.Failure<IReadOnlyList<LibraryEntry>>(library.Error);
    }

    /// <summary>Finds saved nodes by title across the libraries, or in one, without asking any plugin.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="text">The text a title must contain.</param>
    /// <param name="name">One library, or <see langword="null"/> for every library.</param>
    /// <param name="limit">The most entries to return; clamped to 1 to 200.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The entries, each with the library it came from, or why the request was refused.</returns>
    public async Task<Outcome<IReadOnlyList<LibraryEntry>>> SearchAsync(UserId user, string? text, string? name, int limit, CancellationToken cancellationToken)
    {
        string? trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > ContractLimits.MaxQueryTextLength)
        {
            return Outcome.Failure<IReadOnlyList<LibraryEntry>>(
                ErrorCode.InvalidArgument,
                $"'text' must be 1 to {ContractLimits.MaxQueryTextLength} characters.");
        }

        Outcome<LibraryName?> library = await this.ResolveAsync(user, name, cancellationToken).ConfigureAwait(false);
        return library.IsSuccess
            ? Outcome.Success(await store.SearchAsync(user, trimmed, library.Value, Math.Clamp(limit, 1, MaxSearchResults), cancellationToken).ConfigureAwait(false))
            : Outcome.Failure<IReadOnlyList<LibraryEntry>>(library.Error);
    }

    /// <summary>Saves a node. Only nodes the host has served can be saved.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="node">The node.</param>
    /// <param name="name">The library, or <see langword="null"/> for the default one.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the node was added, <see langword="false"/> when it was already saved (it stays where it is: use move); or why it was refused.</returns>
    public async Task<Outcome<bool>> AddAsync(UserId user, NodeRef node, string? name, CancellationToken cancellationToken)
    {
        Outcome<LibraryName?> library = await this.ResolveAsync(user, name, cancellationToken).ConfigureAwait(false);
        if (!library.IsSuccess)
        {
            return Outcome.Failure<bool>(library.Error);
        }

        if (await nodes.GetAsync(node, cancellationToken).ConfigureAwait(false) is null)
        {
            return Outcome.Failure<bool>(ErrorCode.NotFound, "That node has not been seen; browse or search to it first.");
        }

        LibraryEntry entry = new(user, library.Value ?? LibraryName.Default, node, time.GetUtcNow());
        return Outcome.Success(await store.AddAsync(entry, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Removes a saved node from whichever library holds it.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="node">The node.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the node was saved.</returns>
    public Task<bool> RemoveAsync(UserId user, NodeRef node, CancellationToken cancellationToken) =>
        store.RemoveAsync(user, node, cancellationToken);

    /// <summary>Moves a saved node to another library, keeping the date it was added.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="node">The node.</param>
    /// <param name="name">The library to move it to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Success, or <c>not-found</c> when the node is not saved or the library does not exist.</returns>
    public async Task<Outcome<bool>> MoveAsync(UserId user, NodeRef node, string? name, CancellationToken cancellationToken)
    {
        Outcome<LibraryName?> library = await this.ResolveAsync(user, name ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (!library.IsSuccess)
        {
            return Outcome.Failure<bool>(library.Error);
        }

        return await store.MoveAsync(user, node, library.Value!.Value, cancellationToken).ConfigureAwait(false)
            ? Outcome.Success(true)
            : Outcome.Failure<bool>(ErrorCode.NotFound, "That node is not in your library.");
    }

    // A missing name means "no particular library"; a name must be valid and name a library the user has.
    private async Task<Outcome<LibraryName?>> ResolveAsync(UserId user, string? name, CancellationToken cancellationToken)
    {
        if (name is null)
        {
            return Outcome.Success<LibraryName?>(null);
        }

        if (!LibraryName.TryCreate(name, out LibraryName library))
        {
            return Outcome.Failure<LibraryName?>(ErrorCode.InvalidArgument, InvalidName);
        }

        IReadOnlyList<LibrarySummary> libraries = await store.ListLibrariesAsync(user, cancellationToken).ConfigureAwait(false);
        LibrarySummary? found = libraries.FirstOrDefault(candidate => candidate.Name.Matches(library));
        return found is null
            ? Outcome.Failure<LibraryName?>(ErrorCode.NotFound, NoSuchLibrary)
            : Outcome.Success<LibraryName?>(found.Name);
    }
}
