using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <content>The executor: works through the queue, a few jobs at a time.</content>
public sealed partial class DownloadPipeline
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Runner> running = [];
    private readonly SemaphoreSlim wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource stop = new();
    private Task? background;
    private int disposed;

    /// <summary>
    /// Starts working through the queue in the background, until the pipeline is disposed. Jobs a previous run left
    /// running (it was killed) go back in the queue. Calling it again does nothing.
    /// </summary>
    public void Start()
    {
        lock (this.gate)
        {
            if (this.background is not null)
            {
                return;
            }

            CancellationToken token = this.stop.Token;
            this.background = Task.Run(() => this.ServeAsync(token), CancellationToken.None);
        }
    }

    /// <summary>Works through the queue in the foreground and returns when nothing is queued or running. For a command line and for tests; do not combine it with <see cref="Start"/>.</summary>
    /// <param name="cancellationToken">Stops the work; running jobs go back to being queued the next time the executor starts.</param>
    /// <returns>A task that completes when the queue is empty.</returns>
    public Task RunUntilIdleAsync(CancellationToken cancellationToken) => this.WorkAsync(untilIdle: true, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 1)
        {
            return;
        }

        Task? task;
        lock (this.gate)
        {
            task = this.background;
            this.background = null;
        }

        await this.stop.CancelAsync().ConfigureAwait(false);
        if (task is not null)
        {
            await task.ConfigureAwait(false);
        }

        this.stop.Dispose();
        this.wake.Dispose();
    }

    private static DownloadItem Stored(DownloadItem item, string file, string mime, long bytes) =>
        item with { File = file, Mime = mime, Bytes = bytes, ErrorMessage = null };

    private static DownloadJob Counted(DownloadJob job, List<DownloadItem> items, DateTimeOffset now) => job with
    {
        ItemsCompleted = items.Count(item => item.IsStored),
        ItemsTotal = items.Count,
        BytesDownloaded = items.Sum(item => item.Bytes),
        UpdatedAt = now,
    };

    private async Task ServeAsync(CancellationToken stopToken)
    {
        try
        {
            await this.WorkAsync(untilIdle: false, stopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Disposed: the jobs that were running stay Running in the database and are queued again by the next start.
        }
    }

    private async Task WorkAsync(bool untilIdle, CancellationToken stopToken)
    {
        await this.RecoverAsync(stopToken).ConfigureAwait(false);
        using CancellationTokenSource signalStop = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        Task signal = this.wake.WaitAsync(signalStop.Token);
        try
        {
            while (true)
            {
                stopToken.ThrowIfCancellationRequested();
                int waiting = await this.StartQueuedAsync(stopToken).ConfigureAwait(false);
                Task[] active = this.ActiveTasks();
                if (active.Length == 0)
                {
                    if (waiting == 0 && untilIdle)
                    {
                        return;
                    }

                    if (waiting > 0)
                    {
                        // Every queued job changed under us between listing and starting; look again.
                        continue;
                    }
                }

                Task finished = await Task.WhenAny([.. active, signal]).ConfigureAwait(false);
                if (finished == signal)
                {
                    signal = this.wake.WaitAsync(signalStop.Token);
                }

                this.Reap();
            }
        }
        finally
        {
            await signalStop.CancelAsync().ConfigureAwait(false);
            this.InterruptAll();
            await Task.WhenAll(this.ActiveTasks()).ConfigureAwait(false);
            this.Reap();
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow();
        foreach (DownloadJob job in await jobs.ListAsync([DownloadState.Running], int.MaxValue, cancellationToken).ConfigureAwait(false))
        {
            if (!this.IsRunning(job.Id))
            {
                await jobs.TryTransitionAsync(Stamp(job, DownloadState.Queued, now), DownloadState.Running, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Starts as many queued jobs as the limit allows.</summary>
    /// <returns>How many queued jobs were waiting, including those just started.</returns>
    private async Task<int> StartQueuedAsync(CancellationToken stopToken)
    {
        int parallel = await this.GetParallelAsync(stopToken).ConfigureAwait(false);
        IReadOnlyList<DownloadJob> queued = await jobs.ListAsync([DownloadState.Queued], int.MaxValue, stopToken).ConfigureAwait(false);

        // A job that was paused and resumed while its old runner is still stopping waits for that runner to finish.
        List<DownloadJob> pending = [.. queued.Where(job => !this.IsRunning(job.Id)).OrderBy(job => job.CreatedAt).ThenBy(job => job.Id)];
        int free = parallel - this.RunningCount();
        foreach (DownloadJob job in pending)
        {
            if (free <= 0)
            {
                break;
            }

            DateTimeOffset now = time.GetUtcNow();
            DownloadJob started = job with { State = DownloadState.Running, StartedAt = job.StartedAt ?? now, UpdatedAt = now, ErrorMessage = null, CompletedAt = null };
            if (await jobs.TryTransitionAsync(started, DownloadState.Queued, stopToken).ConfigureAwait(false))
            {
                this.Launch(started, stopToken);
                free--;
            }
        }

        return pending.Count;
    }

    private void Launch(DownloadJob job, CancellationToken stopToken)
    {
        Runner runner = new(stopToken);
        runner.Task = Task.Run(() => this.RunGuardedAsync(job, runner.Cancel.Token), CancellationToken.None);
        lock (this.gate)
        {
            this.running[job.Id] = runner;
        }
    }

    private async Task RunGuardedAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        try
        {
            await this.RunJobAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Paused, cancelled, deleted or shutting down: whoever asked has already recorded the state.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Anything else (a full disk, a locked database) must end the job, or it would stay "running" with nothing running it.
            await this.FinishAsync(job, DownloadState.Failed, Truncate($"The download stopped unexpectedly: {exception.Message}")).ConfigureAwait(false);
        }
    }

    private async Task RunJobAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        IReadOnlyList<DownloadItem> items = await jobs.ListItemsAsync(job.Id, cancellationToken).ConfigureAwait(false);
        if (items.Count == 0)
        {
            DownloadWalker walker = new(catalog, nodes);
            Outcome<IReadOnlyList<NodeRef>> leaves = await walker.WalkAsync(job.Node, job.Role, cancellationToken).ConfigureAwait(false);
            if (!leaves.IsSuccess)
            {
                await this.FinishAsync(job, DownloadState.Failed, Truncate(leaves.Error.Message)).ConfigureAwait(false);
                return;
            }

            items = [.. leaves.Value.Select((leaf, position) => new DownloadItem(job.Id, position, leaf, job.Role, null, null, 0, null))];
            await jobs.AddItemsAsync(items, cancellationToken).ConfigureAwait(false);
            job = job with { ItemsTotal = items.Count };
        }

        List<DownloadItem> done = [.. items];
        job = Counted(job, done, time.GetUtcNow());
        if (!await jobs.TryTransitionAsync(job, DownloadState.Running, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        for (int index = 0; index < done.Count; index++)
        {
            if (done[index].IsStored)
            {
                continue;
            }

            done[index] = await this.DownloadItemAsync(job, done[index], cancellationToken).ConfigureAwait(false);
            job = Counted(job, done, time.GetUtcNow());
            if (!await jobs.TryTransitionAsync(job, DownloadState.Running, cancellationToken).ConfigureAwait(false))
            {
                // The user paused, cancelled or deleted the job while this leaf was downloading.
                return;
            }
        }

        DownloadItem? failed = done.Find(item => !item.IsStored);
        if (failed is null)
        {
            await this.FinishAsync(job, DownloadState.Completed, null).ConfigureAwait(false);
            return;
        }

        int failures = done.Count(item => !item.IsStored);
        await this.FinishAsync(job, DownloadState.Failed, Truncate($"{failures} of {done.Count} items could not be downloaded. First error: {failed.ErrorMessage}")).ConfigureAwait(false);
    }

    private async Task<DownloadItem> DownloadItemAsync(DownloadJob job, DownloadItem item, CancellationToken cancellationToken)
    {
        // Another job may have stored this leaf already (a chapter downloaded alone, then its whole series).
        DownloadItem? shared = await jobs.FindStoredAsync(item.Node, item.Role, cancellationToken).ConfigureAwait(false);
        if (shared is { File: { } sharedFile, Mime: { } sharedMime })
        {
            DownloadItem reused = Stored(item, sharedFile, sharedMime, shared.Bytes);
            await jobs.UpdateItemAsync(reused, cancellationToken).ConfigureAwait(false);
            return reused;
        }

        Outcome<IReadOnlyList<Asset>> resolved = await assets.ResolveAsync(item.Node, item.Role, AssetHint.ToProps(job.Hint), cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return await this.FailedItemAsync(item, resolved.Error.Message, cancellationToken).ConfigureAwait(false);
        }

        if (resolved.Value.Count == 0)
        {
            return await this.FailedItemAsync(item, "The plugin resolved no assets for this item.", cancellationToken).ConfigureAwait(false);
        }

        // A leaf has one stored asset: the first the plugin offers, which the hint steers. The rest are alternatives.
        Asset asset = resolved.Value[0];
        Outcome<AssetPayload> payload = await assets.GetPayloadAsync(item.Node, item.Role, asset, cancellationToken).ConfigureAwait(false);
        if (!payload.IsSuccess)
        {
            return await this.FailedItemAsync(item, payload.Error.Message, cancellationToken).ConfigureAwait(false);
        }

        string file = FileNameOf(item.Node, item.Role);
        try
        {
            await files.WriteAsync(file, payload.Value.Payload, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            return await this.FailedItemAsync(item, $"The file could not be written: {exception.Message}", cancellationToken).ConfigureAwait(false);
        }

        DownloadItem stored = Stored(item, file, payload.Value.Mime, payload.Value.Payload.Length);
        await jobs.UpdateItemAsync(stored, cancellationToken).ConfigureAwait(false);
        return stored;
    }

    private async Task<DownloadItem> FailedItemAsync(DownloadItem item, string message, CancellationToken cancellationToken)
    {
        DownloadItem failed = item with { ErrorMessage = Truncate(message) };
        await jobs.UpdateItemAsync(failed, cancellationToken).ConfigureAwait(false);
        return failed;
    }

    private Task<bool> FinishAsync(DownloadJob job, DownloadState state, string? message)
    {
        DateTimeOffset now = time.GetUtcNow();
        DownloadJob finished = job with { State = state, ErrorMessage = message, UpdatedAt = now, CompletedAt = now };

        // Finishing is not interrupted by the job's own cancellation: it only succeeds if the job is still running anyway.
        return jobs.TryTransitionAsync(finished, DownloadState.Running, CancellationToken.None);
    }

    private void Wake()
    {
        if (this.wake.CurrentCount == 0)
        {
            this.wake.Release();
        }
    }

    private bool IsRunning(Guid id)
    {
        lock (this.gate)
        {
            return this.running.ContainsKey(id);
        }
    }

    private int RunningCount()
    {
        lock (this.gate)
        {
            return this.running.Count;
        }
    }

    private Task[] ActiveTasks()
    {
        lock (this.gate)
        {
            return [.. this.running.Values.Select(runner => runner.Task)];
        }
    }

    private void Reap()
    {
        lock (this.gate)
        {
            foreach ((Guid id, Runner runner) in this.running.Where(pair => pair.Value.Task.IsCompleted).ToList())
            {
                runner.Dispose();
                this.running.Remove(id);
            }
        }
    }

    private void Interrupt(Guid id)
    {
        lock (this.gate)
        {
            if (this.running.TryGetValue(id, out Runner? runner))
            {
                runner.Cancel.Cancel();
            }
        }
    }

    private void InterruptAll()
    {
        lock (this.gate)
        {
            foreach (Runner runner in this.running.Values)
            {
                runner.Cancel.Cancel();
            }
        }
    }

    private Task AwaitRunnerAsync(Guid id)
    {
        lock (this.gate)
        {
            return this.running.TryGetValue(id, out Runner? runner) ? runner.Task : Task.CompletedTask;
        }
    }

    /// <summary>One running job: its task, and the token that stops it without stopping the others.</summary>
    /// <param name="stopToken">Fires when the whole executor stops.</param>
    private sealed class Runner(CancellationToken stopToken) : IDisposable
    {
        /// <summary>Gets the token source that stops this job.</summary>
        public CancellationTokenSource Cancel { get; } = CancellationTokenSource.CreateLinkedTokenSource(stopToken);

        /// <summary>Gets or sets the task that runs the job.</summary>
        public Task Task { get; set; } = Task.CompletedTask;

        /// <inheritdoc/>
        public void Dispose() => this.Cancel.Dispose();
    }
}
