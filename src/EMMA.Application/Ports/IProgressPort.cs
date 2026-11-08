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
}
