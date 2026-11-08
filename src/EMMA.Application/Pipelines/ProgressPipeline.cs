using System.Globalization;
using System.Text.RegularExpressions;

using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// Records and reads user progress. One shape serves every media type: the unit
/// (<c>page</c>, <c>ms</c>, <c>paragraph</c>) is an open string the host never interprets.
/// </summary>
/// <param name="store">Where progress is kept.</param>
/// <param name="settings">Where the history retention is kept.</param>
/// <param name="time">The time source that stamps each update.</param>
public sealed partial class ProgressPipeline(IProgressPort store, IAppSettingsPort settings, TimeProvider time)
{
    /// <summary>The unit of a node marked finished without ever having a position.</summary>
    public const string FinishedUnit = "item";

    /// <summary>The most days of history a user can ask to keep: ten years.</summary>
    public const int MaxRetentionDays = 3650;

    private const int MaxRecentEntries = 200;
    private const int MaxSeriesEntries = 10000;

    // The host's own scope (see AppSettingsPipeline.ReservedPrefix), so a client cannot write garbage here.
    private const string RetentionScope = "emma.history";
    private const string RetentionKey = "retention_days";

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
        await this.ApplyRetentionAsync(user, cancellationToken).ConfigureAwait(false);
        return Outcome.Success(progress);
    }

    /// <summary>
    /// Marks nodes finished, for "mark these chapters as read". A node with progress keeps its unit and extent and its
    /// position moves to the end; a node without any is recorded as <see cref="FinishedUnit"/> 1 of 1.
    /// </summary>
    /// <param name="user">Whose progress it is.</param>
    /// <param name="nodes">The nodes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The stored progress, in the order of <paramref name="nodes"/>.</returns>
    public async Task<IReadOnlyList<Progress>> CompleteAsync(UserId user, IReadOnlyList<NodeRef> nodes, CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow();
        List<Progress> stored = [];
        foreach (NodeRef node in nodes.Distinct())
        {
            Progress? existing = await store.GetAsync(user, node, cancellationToken).ConfigureAwait(false);
            Progress finished = existing is null
                ? new Progress(user, node, new ProgressLocator(FinishedUnit, 1, 1), true, now)
                : existing with
                {
                    Locator = existing.Locator with { Value = existing.Locator.Total ?? existing.Locator.Value },
                    Completed = true,
                    UpdatedAt = now,
                };
            await store.UpsertAsync(finished, cancellationToken).ConfigureAwait(false);
            stored.Add(finished);
        }

        await this.ApplyRetentionAsync(user, cancellationToken).ConfigureAwait(false);
        return stored;
    }

    /// <summary>Deletes the progress of nodes and of everything beneath them: a chapter, or a whole series.</summary>
    /// <param name="user">Whose progress it is.</param>
    /// <param name="nodes">The nodes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many entries were deleted.</returns>
    public async Task<int> DeleteAsync(UserId user, IReadOnlyList<NodeRef> nodes, CancellationToken cancellationToken)
    {
        int removed = 0;
        foreach (NodeRef node in nodes.Distinct())
        {
            removed += await store.DeleteWithinAsync(user, node, cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>Deletes all of a user's progress.</summary>
    /// <param name="user">Whose progress it is.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many entries were deleted.</returns>
    public Task<int> ClearAsync(UserId user, CancellationToken cancellationToken) =>
        store.ClearAsync(user, cancellationToken);

    /// <summary>Gets what a user did beneath one container: counts, the latest activity and every node with progress.</summary>
    /// <param name="user">Whose progress it is.</param>
    /// <param name="container">The series, show or album.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The progress; empty when the user did nothing there. At most 10,000 entries are listed.</returns>
    public async Task<SeriesProgress> SeriesAsync(UserId user, NodeRef container, CancellationToken cancellationToken)
    {
        IReadOnlyList<Progress> items = await store.ListWithinAsync(user, container, MaxSeriesEntries, cancellationToken).ConfigureAwait(false);
        int completed = items.Count(item => item.Completed);
        return new SeriesProgress(container, completed, items.Count - completed, items.Count == 0 ? null : items[0].UpdatedAt, items);
    }

    /// <summary>Lists what a user did recently, one entry per series rather than per chapter.</summary>
    /// <param name="user">Whose progress it is.</param>
    /// <param name="limit">The most series to return; clamped to 1 to 200.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The series, the one with the latest activity first.</returns>
    public Task<IReadOnlyList<ProgressGroup>> HistoryAsync(UserId user, int limit, CancellationToken cancellationToken) =>
        store.ListGroupsAsync(user, Math.Clamp(limit, 1, MaxRecentEntries), cancellationToken);

    /// <summary>Gets how long history is kept.</summary>
    /// <param name="user">Whose history it is.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The number of days, or 0 to keep it for ever (the default).</returns>
    public async Task<int> GetRetentionAsync(UserId user, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> stored = await settings.GetAsync(user, RetentionScope, cancellationToken).ConfigureAwait(false);
        return stored.TryGetValue(RetentionKey, out string? text)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int days)
            && days <= MaxRetentionDays
                ? days
                : 0;
    }

    /// <summary>Sets how long history is kept and deletes what is older now. Retention is applied again whenever progress is recorded.</summary>
    /// <param name="user">Whose history it is.</param>
    /// <param name="days">The number of days, 0 to keep it for ever, at most <see cref="MaxRetentionDays"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The setting and how many entries it deleted, or why it was refused.</returns>
    public async Task<Outcome<HistoryRetention>> SetRetentionAsync(UserId user, int days, CancellationToken cancellationToken)
    {
        if (days is < 0 or > MaxRetentionDays)
        {
            return Outcome.Failure<HistoryRetention>(ErrorCode.InvalidArgument, $"'days' must be a whole number from 0 to {MaxRetentionDays}; 0 keeps history for ever.");
        }

        AppSettingChange change = new(RetentionKey, days == 0 ? null : days.ToString(CultureInfo.InvariantCulture));
        await settings.ApplyAsync(user, RetentionScope, [change], time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        int removed = await this.ApplyRetentionAsync(user, cancellationToken).ConfigureAwait(false);
        return Outcome.Success(new HistoryRetention(days, removed));
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

    private async Task<int> ApplyRetentionAsync(UserId user, CancellationToken cancellationToken)
    {
        int days = await this.GetRetentionAsync(user, cancellationToken).ConfigureAwait(false);
        return days == 0
            ? 0
            : await store.DeleteBeforeAsync(user, time.GetUtcNow().AddDays(-days), cancellationToken).ConfigureAwait(false);
    }
}
