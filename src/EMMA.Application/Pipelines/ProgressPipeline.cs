using System.Text.RegularExpressions;

using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// Records and reads user progress. One shape serves every media type: the unit
/// (<c>page</c>, <c>ms</c>, <c>paragraph</c>) is an open string the host never interprets.
/// </summary>
/// <param name="store">Where progress is kept.</param>
/// <param name="time">The time source that stamps each update.</param>
public sealed partial class ProgressPipeline(IProgressPort store, TimeProvider time)
{
    private const int MaxRecentEntries = 200;

    /// <summary>Records where a user is within a node.</summary>
    /// <param name="user">Whose progress it is.</param>
    /// <param name="node">The node: the chapter for paged media, the episode for video.</param>
    /// <param name="locator">The position.</param>
    /// <param name="completed">Whether the user finished the node.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The stored progress, or why the locator was rejected.</returns>
    public async Task<Outcome<Progress>> SetAsync(
        UserId user,
        NodeRef node,
        ProgressLocator locator,
        bool completed,
        CancellationToken cancellationToken)
    {
        string? problem = Validate(locator);
        if (problem is not null)
        {
            return Outcome.Failure<Progress>(ErrorCode.InvalidArgument, problem);
        }

        Progress progress = new(user, node, locator, completed, time.GetUtcNow());
        await store.UpsertAsync(progress, cancellationToken).ConfigureAwait(false);
        return Outcome.Success(progress);
    }

    /// <summary>Gets a user's progress for a node.</summary>
    /// <param name="user">The user.</param>
    /// <param name="node">The node.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The progress, or <see langword="null"/> when none.</returns>
    public Task<Progress?> GetAsync(UserId user, NodeRef node, CancellationToken cancellationToken) =>
        store.GetAsync(user, node, cancellationToken);

    /// <summary>Gets where to continue within a container: the latest progress of it or anything beneath it.</summary>
    /// <param name="user">The user.</param>
    /// <param name="container">The series, show or album.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The latest progress, or <see langword="null"/> when none.</returns>
    public Task<Progress?> ContinueAsync(UserId user, NodeRef container, CancellationToken cancellationToken) =>
        store.GetLatestWithinAsync(user, container, cancellationToken);

    /// <summary>Lists a user's recent progress, most recent first.</summary>
    /// <param name="user">The user.</param>
    /// <param name="limit">The most entries to return; clamped to 1 to 200.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The entries.</returns>
    public Task<IReadOnlyList<Progress>> RecentAsync(UserId user, int limit, CancellationToken cancellationToken) =>
        store.ListRecentAsync(user, Math.Clamp(limit, 1, MaxRecentEntries), cancellationToken);

    private static string? Validate(ProgressLocator locator)
    {
        if (!UnitPattern().IsMatch(locator.Unit))
        {
            return "The progress unit must be a short lower-case token such as 'page' or 'ms'.";
        }

        if (!double.IsFinite(locator.Value) || locator.Value < 0)
        {
            return "The progress value must be zero or more.";
        }

        if (locator.Total is { } total && (!double.IsFinite(total) || total < locator.Value))
        {
            return "The progress total must be at least the value.";
        }

        return null;
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex UnitPattern();
}
