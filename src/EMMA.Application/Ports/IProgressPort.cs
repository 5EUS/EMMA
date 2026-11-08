using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>Stores user progress. One shape for every media type.</summary>
public interface IProgressPort
{
    /// <summary>Inserts or replaces a user's progress for a node.</summary>
    /// <param name="progress">The progress to store.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the progress is stored.</returns>
    Task UpsertAsync(Progress progress, CancellationToken cancellationToken);

    /// <summary>Gets a user's progress for a node.</summary>
    /// <param name="user">The user.</param>
    /// <param name="node">The node.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The progress, or <see langword="null"/> when none.</returns>
    Task<Progress?> GetAsync(UserId user, NodeRef node, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the most recently updated progress of a node or any of its descendants:
    /// "continue reading" for a series, "continue watching" for a show.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="ancestor">The container node.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The latest progress, or <see langword="null"/> when none.</returns>
    Task<Progress?> GetLatestWithinAsync(UserId user, NodeRef ancestor, CancellationToken cancellationToken);

    /// <summary>Lists a user's progress, most recent first.</summary>
    /// <param name="user">The user.</param>
    /// <param name="limit">The most entries to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The entries.</returns>
    Task<IReadOnlyList<Progress>> ListRecentAsync(UserId user, int limit, CancellationToken cancellationToken);

    /// <summary>Lists the progress of a node and of everything beneath it.</summary>
    /// <param name="user">The user.</param>
    /// <param name="ancestor">The container node.</param>
    /// <param name="limit">The most entries to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The entries, most recent first.</returns>
    Task<IReadOnlyList<Progress>> ListWithinAsync(UserId user, NodeRef ancestor, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Groups a user's progress by series: the topmost node the node cache holds above each progress entry, or the
    /// node itself when its parents are not cached.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="limit">The most groups to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The groups, the one with the latest activity first.</returns>
    Task<IReadOnlyList<ProgressGroup>> ListGroupsAsync(UserId user, int limit, CancellationToken cancellationToken);

    /// <summary>Deletes the progress of a node and of everything beneath it.</summary>
    /// <param name="user">The user.</param>
    /// <param name="root">The node.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many entries were deleted.</returns>
    Task<int> DeleteWithinAsync(UserId user, NodeRef root, CancellationToken cancellationToken);

    /// <summary>Deletes all of a user's progress.</summary>
    /// <param name="user">The user.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many entries were deleted.</returns>
    Task<int> ClearAsync(UserId user, CancellationToken cancellationToken);

    /// <summary>Deletes a user's progress that was last updated before a moment.</summary>
    /// <param name="user">The user.</param>
    /// <param name="before">Entries updated before this are deleted.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many entries were deleted.</returns>
    Task<int> DeleteBeforeAsync(UserId user, DateTimeOffset before, CancellationToken cancellationToken);
}
