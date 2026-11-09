using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// Downloads: a job over a node and an asset role. One executor serves every media type: it walks the node's
/// children down to the leaves whose kind declares the role, resolves each through <see cref="AssetPipeline"/>
/// and keeps the bytes in the downloads directory, where <see cref="ReadStoredAsync"/> serves them without
/// touching the network. This class is the job list (queue, pause, retry, delete); the executor that works the
/// queue is in <c>DownloadPipeline.Worker.cs</c> and runs only after <see cref="Start"/> or
/// <see cref="RunUntilIdleAsync"/>, so a host that merely looks at the list never downloads anything.
/// Downloads belong to the host, not to a user: every client sees and controls the same queue.
/// </summary>
/// <param name="jobs">Where jobs and their leaves are kept.</param>
/// <param name="files">Where downloaded bytes are kept.</param>
/// <param name="catalog">Lists the children a job walks.</param>
/// <param name="assets">Resolves and fetches each leaf.</param>
/// <param name="nodes">Remembers the nodes the host has served; a job can only start from one of them.</param>
/// <param name="settings">Where the parallel-downloads limit is kept.</param>
/// <param name="time">The time source that stamps each change.</param>
public sealed partial class DownloadPipeline(
    IDownloadPort jobs,
    IDownloadFilePort files,
    CatalogPipeline catalog,
    AssetPipeline assets,
    INodeStorePort nodes,
    IAppSettingsPort settings,
    TimeProvider time) : IAsyncDisposable
{
    /// <summary>The most jobs that may run at once.</summary>
    public const int MaxParallel = 8;

    /// <summary>The jobs that run at once until the user chooses otherwise.</summary>
    public const int DefaultParallel = 2;

    private const int MaxListed = 1000;
    private const int MaxErrorLength = 512;
    private const string ParallelScope = "emma.downloads";
    private const string ParallelKey = "parallel";

    // Downloads belong to the host, so their one setting is kept under a user no client can be (a client id has
    // no colon in it by convention) and in the host's own scope (see AppSettingsPipeline.ReservedPrefix).
    private static readonly UserId HostUser = UserId.Create("emma:host");

    /// <summary>Queues a download, or returns the job already there for that node and role.</summary>
    /// <param name="node">The node: a chapter, a season, a whole series or a single leaf.</param>
    /// <param name="role">The asset role to store.</param>
    /// <param name="hint">A preference passed to the plugin when each asset is resolved, such as a quality (<c>1080p</c>); <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The job. A job that is queued, running, paused or completed for the same node and role is returned as it is (the
    /// hint is not changed); one that failed or was cancelled is queued again.
    /// </returns>
    public async Task<Outcome<DownloadJob>> EnqueueAsync(NodeRef node, AssetRole role, string? hint, CancellationToken cancellationToken)
    {
        if (!AssetHint.IsValid(hint))
        {
            return Outcome.Failure<DownloadJob>(ErrorCode.InvalidArgument, $"'hint' must be 1 to {AssetHint.MaxLength} characters without control characters.");
        }

        if (await nodes.GetAsync(node, cancellationToken).ConfigureAwait(false) is null)
        {
            return Outcome.Failure<DownloadJob>(ErrorCode.NotFound, "The host has not served that node; open it before downloading.");
        }

        DownloadJob? existing = await jobs.FindAsync(node, role, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.State is DownloadState.Failed or DownloadState.Canceled
                ? await this.ChangeAsync(existing.Id, "retried", job => Requeue(job, time.GetUtcNow()), cancellationToken).ConfigureAwait(false)
                : Outcome.Success(existing);
        }

        DateTimeOffset now = time.GetUtcNow();
        DownloadJob created = new(Guid.NewGuid(), node, role, hint, DownloadState.Queued, 0, 0, 0, null, now, now, null, null);
        await jobs.CreateAsync(created, cancellationToken).ConfigureAwait(false);
        this.Wake();
        return Outcome.Success(created);
    }

    /// <summary>Lists jobs, the most recently changed first.</summary>
    /// <param name="states">Only jobs in these states, or <see langword="null"/> for all.</param>
    /// <param name="limit">The most jobs to return; clamped to 1 to 1000.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The jobs.</returns>
    public Task<IReadOnlyList<DownloadJob>> ListAsync(IReadOnlyCollection<DownloadState>? states, int limit, CancellationToken cancellationToken) =>
        jobs.ListAsync(states, Math.Clamp(limit, 1, MaxListed), cancellationToken);

    /// <summary>Gets a job and the leaves it stores, for a client that reads offline.</summary>
    /// <param name="id">The job id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The job and its leaves, or <c>not-found</c>.</returns>
    public async Task<Outcome<DownloadDetail>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        DownloadJob? job = await jobs.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return job is null
            ? Outcome.Failure<DownloadDetail>(ErrorCode.NotFound, "That download does not exist.")
            : Outcome.Success(new DownloadDetail(job, await jobs.ListItemsAsync(id, cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>Pauses a queued or running job. What it has stored is kept.</summary>
    /// <param name="id">The job id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The job as it now stands, or why it cannot be paused.</returns>
    public async Task<Outcome<DownloadJob>> PauseAsync(Guid id, CancellationToken cancellationToken)
    {
        Outcome<DownloadJob> paused = await this.ChangeAsync(
            id,
            "paused",
            job => job.State is DownloadState.Queued or DownloadState.Running ? Stamp(job, DownloadState.Paused, time.GetUtcNow()) : null,
            cancellationToken).ConfigureAwait(false);
        if (paused.IsSuccess)
        {
            this.Interrupt(id);
        }

        return paused;
    }

    /// <summary>Queues a paused job again; it carries on with what is not stored yet.</summary>
    /// <param name="id">The job id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The job as it now stands, or why it cannot be resumed.</returns>
    public Task<Outcome<DownloadJob>> ResumeAsync(Guid id, CancellationToken cancellationToken) =>
        this.ChangeAsync(id, "resumed", job => job.State == DownloadState.Paused ? Requeue(job, time.GetUtcNow()) : null, cancellationToken);

    /// <summary>Queues a failed or cancelled job again; it carries on with what is not stored yet.</summary>
    /// <param name="id">The job id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The job as it now stands, or why it cannot be retried.</returns>
    public Task<Outcome<DownloadJob>> RetryAsync(Guid id, CancellationToken cancellationToken) =>
        this.ChangeAsync(
            id,
            "retried",
            job => job.State is DownloadState.Failed or DownloadState.Canceled ? Requeue(job, time.GetUtcNow()) : null,
            cancellationToken);

    /// <summary>Cancels a queued, running or paused job. What it has stored is kept until the job is deleted.</summary>
    /// <param name="id">The job id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The job as it now stands, or why it cannot be cancelled.</returns>
    public async Task<Outcome<DownloadJob>> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        Outcome<DownloadJob> cancelled = await this.ChangeAsync(
            id,
            "cancelled",
            job => job.State is DownloadState.Queued or DownloadState.Running or DownloadState.Paused
                ? Stamp(job, DownloadState.Canceled, time.GetUtcNow()) with { CompletedAt = time.GetUtcNow() }
                : null,
            cancellationToken).ConfigureAwait(false);
        if (cancelled.IsSuccess)
        {
            this.Interrupt(id);
        }

        return cancelled;
    }

    /// <summary>Deletes a job and the files that no other job's leaves share. A running job is stopped first.</summary>
    /// <param name="id">The job id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the job existed.</returns>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await jobs.GetAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        this.Interrupt(id);
        await this.AwaitRunnerAsync(id).ConfigureAwait(false);
        IReadOnlyList<string> exclusive = await jobs.ListExclusiveFilesAsync(id, cancellationToken).ConfigureAwait(false);
        bool existed = await jobs.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        await files.DeleteAsync([.. exclusive], cancellationToken).ConfigureAwait(false);
        return existed;
    }

    /// <summary>Deletes every job in the given states, with their files.</summary>
    /// <param name="states">The states to clear; at least one.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many jobs were deleted, or why the call was refused.</returns>
    public async Task<Outcome<int>> ClearAsync(IReadOnlyCollection<DownloadState> states, CancellationToken cancellationToken)
    {
        if (states.Count == 0)
        {
            return Outcome.Failure<int>(ErrorCode.InvalidArgument, "Name at least one state to clear.");
        }

        int deleted = 0;
        foreach (DownloadJob job in await jobs.ListAsync(states, int.MaxValue, cancellationToken).ConfigureAwait(false))
        {
            if (await this.DeleteAsync(job.Id, cancellationToken).ConfigureAwait(false))
            {
                deleted++;
            }
        }

        return Outcome.Success(deleted);
    }

    /// <summary>Queues every failed job again.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many jobs were queued.</returns>
    public async Task<int> RetryFailedAsync(CancellationToken cancellationToken)
    {
        int queued = 0;
        foreach (DownloadJob job in await jobs.ListAsync([DownloadState.Failed], int.MaxValue, cancellationToken).ConfigureAwait(false))
        {
            if ((await this.RetryAsync(job.Id, cancellationToken).ConfigureAwait(false)).IsSuccess)
            {
                queued++;
            }
        }

        return queued;
    }

    /// <summary>Gets how many jobs run at once.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The limit, <see cref="DefaultParallel"/> until one is set.</returns>
    public async Task<int> GetParallelAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> stored = await settings.GetAsync(HostUser, ParallelScope, cancellationToken).ConfigureAwait(false);
        return stored.TryGetValue(ParallelKey, out string? text)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int parallel)
            && parallel is >= 1 and <= MaxParallel
                ? parallel
                : DefaultParallel;
    }

    /// <summary>Sets how many jobs run at once. The executor applies it before it starts the next job.</summary>
    /// <param name="parallel">The limit, 1 to <see cref="MaxParallel"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The limit, or why it was refused.</returns>
    public async Task<Outcome<int>> SetParallelAsync(int parallel, CancellationToken cancellationToken)
    {
        if (parallel is < 1 or > MaxParallel)
        {
            return Outcome.Failure<int>(ErrorCode.InvalidArgument, $"'parallel' must be a whole number from 1 to {MaxParallel}.");
        }

        AppSettingChange change = new(ParallelKey, parallel.ToString(CultureInfo.InvariantCulture));
        await settings.ApplyAsync(HostUser, ParallelScope, [change], time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        this.Wake();
        return Outcome.Success(parallel);
    }

    /// <summary>Reads an asset from a completed download.</summary>
    /// <param name="node">The leaf node.</param>
    /// <param name="role">The asset role.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The stored asset, or <see langword="null"/> when nothing is stored for the leaf (or its file is gone), in which case the caller goes to the plugin.</returns>
    public async Task<AssetPayload?> ReadStoredAsync(NodeRef node, AssetRole role, CancellationToken cancellationToken)
    {
        DownloadItem? item = await jobs.FindStoredAsync(node, role, cancellationToken).ConfigureAwait(false);
        if (item?.File is not { } file)
        {
            return null;
        }

        byte[]? bytes = await files.ReadAsync(file, cancellationToken).ConfigureAwait(false);
        return bytes is null
            ? null
            : new AssetPayload(item.Mime ?? "application/octet-stream", bytes, time.GetUtcNow());
    }

    private static DownloadJob Stamp(DownloadJob job, DownloadState state, DateTimeOffset now) =>
        job with { State = state, UpdatedAt = now };

    private static DownloadJob Requeue(DownloadJob job, DateTimeOffset now) =>
        job with { State = DownloadState.Queued, ErrorMessage = null, CompletedAt = null, UpdatedAt = now };

    private static string FileNameOf(NodeRef node, AssetRole role) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{node.Plugin}\n{node.Id}\n{role}")));

    private static string Truncate(string text) =>
        text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];

    private static string StateName(DownloadState state) => state switch
    {
        DownloadState.Queued => "queued",
        DownloadState.Running => "running",
        DownloadState.Paused => "paused",
        DownloadState.Completed => "completed",
        DownloadState.Failed => "failed",
        _ => "cancelled",
    };

    private async Task<Outcome<DownloadJob>> ChangeAsync(
        Guid id,
        string action,
        Func<DownloadJob, DownloadJob?> next,
        CancellationToken cancellationToken)
    {
        DownloadJob? job = await jobs.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return Outcome.Failure<DownloadJob>(ErrorCode.NotFound, "That download does not exist.");
        }

        DownloadJob? changed = next(job);
        if (changed is null)
        {
            return Outcome.Failure<DownloadJob>(ErrorCode.InvalidArgument, $"A {StateName(job.State)} download cannot be {action}.");
        }

        if (!await jobs.TryTransitionAsync(changed, job.State, cancellationToken).ConfigureAwait(false))
        {
            return Outcome.Failure<DownloadJob>(ErrorCode.Unavailable, "The download changed while it was being updated; try again.");
        }

        this.Wake();
        return Outcome.Success(changed);
    }
}
