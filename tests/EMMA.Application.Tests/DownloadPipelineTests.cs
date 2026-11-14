using System.Text;

using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.Infrastructure.Cache;
using EMMA.Infrastructure.InMemory;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.Application.Tests;

/// <summary>Tests for <see cref="DownloadPipeline"/>: the job list and the executor that works it.</summary>
public sealed class DownloadPipelineTests : IAsyncDisposable
{
    private static readonly AssetRole Image = AssetRole.Create("image");

    // Never set: waiting on it is a short pause that lets other jobs' threads run.
    private static readonly ManualResetEventSlim Overlap = new(false);

    private readonly FakeTimeProvider time = new();
    private readonly FakePluginGateway gateway = new();
    private readonly InMemoryNodeStore nodes = new();
    private readonly FakeDownloadStore jobs = new();
    private readonly FakeDownloadFiles files = new();
    private readonly FakeAppSettings settings = new();
    private readonly DownloadPipeline downloads;
    private readonly List<string> resolved = [];
    private readonly List<IReadOnlyList<Prop>> hints = [];

    /// <summary>Initializes a new instance of the <see cref="DownloadPipelineTests"/> class.</summary>
    public DownloadPipelineTests()
    {
        PluginCallExecutor executor = new(new PluginCallOptions(1, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(30)), this.time);
        CatalogPipeline catalog = new(this.gateway, this.nodes, new InMemoryNodeListingStore(), executor, this.time);
        AssetPipeline assets = new(this.gateway, new FakePolicies(), this.nodes, new FakeAssetFetcher(this.time), new BoundedAssetCache(new AssetCacheOptions(1024 * 1024, 0, "unused", TimeSpan.FromDays(1)), this.time), new BoundedResolutionCache(64, this.time), executor, new AssetPipelineOptions(1024), this.time);
        this.downloads = new DownloadPipeline(this.jobs, this.files, catalog, assets, this.nodes, this.settings, this.time);

        // series s1 -> chapters c1 (pages c1p1, c1p2) and c2 (page c2p1); a page's bytes are its id.
        this.gateway.OnChildren = (_, parent, _) => Outcome.Success(parent?.Value switch
        {
            "s1" => TestData.RawPage(null, TestData.RawNode("c1", "chapter"), TestData.RawNode("c2", "chapter")),
            "c1" => TestData.RawPage(null, TestData.RawNode("c1p1", "page"), TestData.RawNode("c1p2", "page")),
            "c2" => TestData.RawPage(null, TestData.RawNode("c2p1", "page")),
            _ => TestData.RawPage(null),
        });
        this.gateway.OnResolve = (_, request) =>
        {
            lock (this.resolved)
            {
                this.resolved.Add(request.Node.Value);
                this.hints.Add(request.Hints);
            }

            return Outcome.Success<IReadOnlyList<Asset>>([new Asset("image/png", new InlineDelivery(Encoding.UTF8.GetBytes(request.Node.Value)), null, [])]);
        };
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => this.downloads.DisposeAsync();

    /// <summary>A job can only start from a node the host has served.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task EnqueueNeedsAKnownNode()
    {
        Outcome<DownloadJob> job = await this.downloads.EnqueueAsync(TestData.Ref("c1"), Image, null, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.NotFound, job.Error?.Code);
    }

    /// <summary>A hint that is empty or has control characters is refused.</summary>
    /// <param name="hint">The hint.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("")]
    [InlineData("720p\n")]
    public async Task EnqueueRefusesABadHint(string hint)
    {
        await this.KnowAsync("c1", "chapter").ConfigureAwait(true);

        Outcome<DownloadJob> job = await this.downloads.EnqueueAsync(TestData.Ref("c1"), Image, hint, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, job.Error?.Code);
    }

    /// <summary>A chapter job walks to its pages, stores each one and counts them.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ADownloadStoresEveryLeaf()
    {
        DownloadJob queued = await this.EnqueueAsync("c1", "chapter").ConfigureAwait(true);
        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);

        DownloadDetail detail = (await this.downloads.GetAsync(queued.Id, CancellationToken.None).ConfigureAwait(true)).Value;

        Assert.Equal(DownloadState.Completed, detail.Job.State);
        Assert.Equal(2, detail.Job.ItemsCompleted);
        Assert.Equal(2, detail.Job.ItemsTotal);
        Assert.Equal(8, detail.Job.BytesDownloaded);
        Assert.NotNull(detail.Job.CompletedAt);
        Assert.Equal([TestData.Ref("c1p1"), TestData.Ref("c1p2")], detail.Items.Select(item => item.Node));
        Assert.All(detail.Items, item => Assert.True(item.IsStored));
        AssetPayload? stored = await this.downloads.ReadStoredAsync(TestData.Ref("c1p2"), Image, CancellationToken.None).ConfigureAwait(true);
        Assert.Equal("c1p2", Encoding.UTF8.GetString(stored!.Payload.Span));
        Assert.Equal("image/png", stored.Mime);
    }

    /// <summary>A series job finds the pages beneath every chapter, in the order the plugin lists them.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ASeriesDownloadWalksEveryLevel()
    {
        DownloadJob queued = await this.EnqueueAsync("s1", "series").ConfigureAwait(true);
        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);

        DownloadDetail detail = (await this.downloads.GetAsync(queued.Id, CancellationToken.None).ConfigureAwait(true)).Value;

        Assert.Equal(DownloadState.Completed, detail.Job.State);
        Assert.Equal(["c1p1", "c1p2", "c2p1"], detail.Items.Select(item => item.Node.Id.Value));
    }

    /// <summary>Asking again for something already queued or done returns the same job and does no more work.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task EnqueueingTwiceReturnsTheSameJob()
    {
        DownloadJob first = await this.EnqueueAsync("c1", "chapter").ConfigureAwait(true);
        DownloadJob queuedAgain = await this.EnqueueAsync("c1", "chapter").ConfigureAwait(true);
        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);
        int resolves = this.resolved.Count;
        DownloadJob doneAgain = await this.EnqueueAsync("c1", "chapter").ConfigureAwait(true);
        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(first.Id, queuedAgain.Id);
        Assert.Equal(first.Id, doneAgain.Id);
        Assert.Equal(DownloadState.Completed, doneAgain.State);
        Assert.Equal(resolves, this.resolved.Count);
    }

    /// <summary>The hint reaches the plugin as the quality preference.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TheHintIsPassedToThePlugin()
    {
        await this.KnowAsync("c2", "chapter").ConfigureAwait(true);
        await this.downloads.EnqueueAsync(TestData.Ref("c2"), Image, "720p", CancellationToken.None).ConfigureAwait(true);
        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);

        Prop hint = Assert.Single(this.hints.Single());
        Assert.Equal("emma.quality", hint.Key);
        Assert.Equal("720p", hint.Value.Text);
    }

    /// <summary>A leaf that cannot be downloaded fails the job with a count; retrying fetches only what is missing.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RetryDownloadsOnlyWhatIsMissing()
    {
        Func<PluginId, ResolveRequest, Outcome<IReadOnlyList<Asset>>> working = this.gateway.OnResolve;
        this.gateway.OnResolve = (plugin, request) => string.Equals(request.Node.Value, "c1p2", StringComparison.Ordinal)
            ? Outcome.Failure<IReadOnlyList<Asset>>(ErrorCode.NotFound, "gone")
            : working(plugin, request);
        DownloadJob queued = await this.EnqueueAsync("c1", "chapter").ConfigureAwait(true);
        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);

        DownloadJob failed = (await this.downloads.GetAsync(queued.Id, CancellationToken.None).ConfigureAwait(true)).Value.Job;
        Assert.Equal(DownloadState.Failed, failed.State);
        Assert.Equal(1, failed.ItemsCompleted);
        Assert.Contains("1 of 2", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("gone", failed.ErrorMessage, StringComparison.Ordinal);

        this.gateway.OnResolve = working;
        this.resolved.Clear();
        Outcome<DownloadJob> retried = await this.downloads.RetryAsync(queued.Id, CancellationToken.None).ConfigureAwait(true);
        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(DownloadState.Queued, retried.Value.State);
        Assert.Null(retried.Value.ErrorMessage);
        Assert.Equal(DownloadState.Completed, (await this.downloads.GetAsync(queued.Id, CancellationToken.None).ConfigureAwait(true)).Value.Job.State);
        Assert.Equal(["c1p2"], this.resolved);
    }

    /// <summary>Enqueueing a failed job again retries it instead of creating a second job.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task EnqueueingAFailedJobRetriesIt()
    {
        DownloadJob queued = await this.EnqueueAsync("c2", "chapter").ConfigureAwait(true);
        await this.jobs.UpdateAsync(queued with { State = DownloadState.Failed, ErrorMessage = "boom" }, CancellationToken.None).ConfigureAwait(true);

        DownloadJob again = await this.EnqueueAsync("c2", "chapter").ConfigureAwait(true);

        Assert.Equal(queued.Id, again.Id);
        Assert.Equal(DownloadState.Queued, again.State);
        Assert.Single(await this.downloads.ListAsync(null, 10, CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>Two jobs that cover the same leaf store it once.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ALeafAnotherJobStoredIsNotFetchedAgain()
    {
        await this.EnqueueAsync("c1", "chapter").ConfigureAwait(true);
        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);
        this.resolved.Clear();

        DownloadJob series = await this.EnqueueAsync("s1", "series").ConfigureAwait(true);
        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(["c2p1"], this.resolved);
        Assert.Equal(3, (await this.downloads.GetAsync(series.Id, CancellationToken.None).ConfigureAwait(true)).Value.Job.ItemsCompleted);
        Assert.Equal(3, this.files.Names.Count);
    }

    /// <summary>Deleting a job removes the files only it used and keeps those another job still names.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DeletingAJobKeepsSharedFiles()
    {
        DownloadJob chapter = await this.EnqueueAsync("c1", "chapter").ConfigureAwait(true);
        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);
        DownloadJob series = await this.EnqueueAsync("s1", "series").ConfigureAwait(true);
        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.True(await this.downloads.DeleteAsync(series.Id, CancellationToken.None).ConfigureAwait(true));
        Assert.Equal(2, this.files.Names.Count);
        Assert.NotNull(await this.downloads.ReadStoredAsync(TestData.Ref("c1p1"), Image, CancellationToken.None).ConfigureAwait(true));
        Assert.Null(await this.downloads.ReadStoredAsync(TestData.Ref("c2p1"), Image, CancellationToken.None).ConfigureAwait(true));

        Assert.True(await this.downloads.DeleteAsync(chapter.Id, CancellationToken.None).ConfigureAwait(true));
        Assert.Empty(this.files.Names);
        Assert.False(await this.downloads.DeleteAsync(chapter.Id, CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>A stored leaf whose file was deleted from disk is not served, so the caller falls back to the plugin.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AMissingFileIsNotServed()
    {
        await this.EnqueueAsync("c2", "chapter").ConfigureAwait(true);
        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);
        await this.files.DeleteAsync([.. this.files.Names], CancellationToken.None).ConfigureAwait(true);

        Assert.Null(await this.downloads.ReadStoredAsync(TestData.Ref("c2p1"), Image, CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>A node with nothing beneath it that has the role fails with a reason instead of completing empty.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task NothingToDownloadFailsTheJob()
    {
        DownloadJob queued = await this.EnqueueAsync("empty", "chapter").ConfigureAwait(true);
        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);

        DownloadJob job = (await this.downloads.GetAsync(queued.Id, CancellationToken.None).ConfigureAwait(true)).Value.Job;

        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Contains("Nothing beneath", job.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>A job a killed process left running is queued again and finished.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AJobLeftRunningIsRecovered()
    {
        await this.KnowAsync("c2", "chapter").ConfigureAwait(true);
        DateTimeOffset now = this.time.GetUtcNow();
        DownloadJob stale = new(Guid.NewGuid(), TestData.Ref("c2"), Image, null, DownloadState.Running, 0, 0, 0, null, now, now, now, null);
        await this.jobs.CreateAsync(stale, CancellationToken.None).ConfigureAwait(true);

        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(DownloadState.Completed, (await this.downloads.GetAsync(stale.Id, CancellationToken.None).ConfigureAwait(true)).Value.Job.State);
    }

    /// <summary>Only a job in a suitable state can be paused, resumed, retried or cancelled.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task StateChangesAreChecked()
    {
        DownloadJob queued = await this.EnqueueAsync("c2", "chapter").ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, (await this.downloads.ResumeAsync(queued.Id, CancellationToken.None).ConfigureAwait(true)).Error?.Code);
        Assert.Equal(ErrorCode.InvalidArgument, (await this.downloads.RetryAsync(queued.Id, CancellationToken.None).ConfigureAwait(true)).Error?.Code);
        Assert.Equal(DownloadState.Paused, (await this.downloads.PauseAsync(queued.Id, CancellationToken.None).ConfigureAwait(true)).Value.State);
        Assert.Equal(ErrorCode.InvalidArgument, (await this.downloads.PauseAsync(queued.Id, CancellationToken.None).ConfigureAwait(true)).Error?.Code);
        Assert.Equal(DownloadState.Queued, (await this.downloads.ResumeAsync(queued.Id, CancellationToken.None).ConfigureAwait(true)).Value.State);
        Assert.Equal(DownloadState.Canceled, (await this.downloads.CancelAsync(queued.Id, CancellationToken.None).ConfigureAwait(true)).Value.State);
        Assert.Equal(ErrorCode.InvalidArgument, (await this.downloads.CancelAsync(queued.Id, CancellationToken.None).ConfigureAwait(true)).Error?.Code);
        Assert.Equal(ErrorCode.NotFound, (await this.downloads.PauseAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(true)).Error?.Code);
    }

    /// <summary>A paused job is not worked on, and carries on from where it was when resumed.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task APausedJobWaitsUntilResumed()
    {
        DownloadJob queued = await this.EnqueueAsync("c1", "chapter").ConfigureAwait(true);
        await this.downloads.PauseAsync(queued.Id, CancellationToken.None).ConfigureAwait(true);

        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(DownloadState.Paused, (await this.downloads.GetAsync(queued.Id, CancellationToken.None).ConfigureAwait(true)).Value.Job.State);
        Assert.Empty(this.resolved);

        await this.downloads.ResumeAsync(queued.Id, CancellationToken.None).ConfigureAwait(true);
        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(DownloadState.Completed, (await this.downloads.GetAsync(queued.Id, CancellationToken.None).ConfigureAwait(true)).Value.Job.State);
    }

    /// <summary>Pausing a running job stops it after the leaf in flight, and the user's state wins over the executor's.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task PausingARunningJobSticks()
    {
        TaskCompletionSource inFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new(false);
        Func<PluginId, ResolveRequest, Outcome<IReadOnlyList<Asset>>> working = this.gateway.OnResolve;
        this.gateway.OnResolve = (plugin, request) =>
        {
            inFlight.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
            return working(plugin, request);
        };
        DownloadJob queued = await this.EnqueueAsync("c1", "chapter").ConfigureAwait(true);
        this.downloads.Start();

        await inFlight.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Outcome<DownloadJob> paused = await this.downloads.PauseAsync(queued.Id, CancellationToken.None).ConfigureAwait(true);
        release.Set();
        await this.DisposeAsync().ConfigureAwait(true);

        DownloadJob job = (await this.downloads.GetAsync(queued.Id, CancellationToken.None).ConfigureAwait(true)).Value.Job;
        Assert.Equal(DownloadState.Paused, paused.Value.State);
        Assert.Equal(DownloadState.Paused, job.State);
        Assert.True(job.ItemsCompleted < 2);
    }

    /// <summary>The executor in the background finishes queued jobs without being asked.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TheBackgroundExecutorWorksTheQueue()
    {
        this.downloads.Start();
        this.downloads.Start();
        DownloadJob queued = await this.EnqueueAsync("c1", "chapter").ConfigureAwait(true);

        DownloadState state = await this.WaitForFinalStateAsync(queued.Id).ConfigureAwait(true);

        Assert.Equal(DownloadState.Completed, state);
    }

    /// <summary>No more jobs run at once than the limit allows, and all of them finish.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TheParallelLimitIsHonoured()
    {
        int current = 0;
        int peak = 0;
        Func<PluginId, ResolveRequest, Outcome<IReadOnlyList<Asset>>> working = this.gateway.OnResolve;
        this.gateway.OnResolve = (plugin, request) =>
        {
            int now = Interlocked.Increment(ref current);
            int seen;
            do
            {
                seen = Volatile.Read(ref peak);
            }
            while (now > seen && Interlocked.CompareExchange(ref peak, now, seen) != seen);

            Overlap.Wait(TimeSpan.FromMilliseconds(2));
            Interlocked.Decrement(ref current);
            return working(plugin, request);
        };
        await this.downloads.SetParallelAsync(1, CancellationToken.None).ConfigureAwait(true);
        List<DownloadJob> queued = [];
        foreach (string id in new[] { "c1", "c2", "c1p1", "c1p2" })
        {
            queued.Add(await this.EnqueueAsync(id, id.Length == 2 ? "chapter" : "page").ConfigureAwait(true));
        }

        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(1, peak);
        foreach (DownloadJob job in queued)
        {
            Assert.Equal(DownloadState.Completed, (await this.downloads.GetAsync(job.Id, CancellationToken.None).ConfigureAwait(true)).Value.Job.State);
        }
    }

    /// <summary>The parallel limit defaults, is bounded, and is remembered.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TheParallelLimitIsBoundedAndRemembered()
    {
        Assert.Equal(DownloadPipeline.DefaultParallel, await this.downloads.GetParallelAsync(CancellationToken.None).ConfigureAwait(true));
        Assert.Equal(ErrorCode.InvalidArgument, (await this.downloads.SetParallelAsync(0, CancellationToken.None).ConfigureAwait(true)).Error?.Code);
        Assert.Equal(ErrorCode.InvalidArgument, (await this.downloads.SetParallelAsync(DownloadPipeline.MaxParallel + 1, CancellationToken.None).ConfigureAwait(true)).Error?.Code);

        Assert.Equal(5, (await this.downloads.SetParallelAsync(5, CancellationToken.None).ConfigureAwait(true)).Value);
        Assert.Equal(5, await this.downloads.GetParallelAsync(CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>Clearing deletes only the named states and needs at least one; retry-failed queues every failed job.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ClearAndRetryFailedWorkInBulk()
    {
        DownloadJob done = await this.EnqueueAsync("c2", "chapter").ConfigureAwait(true);
        await this.downloads.RunUntilIdleAsync(CancellationToken.None).ConfigureAwait(true);
        DownloadJob failed = await this.EnqueueAsync("c1", "chapter").ConfigureAwait(true);
        await this.jobs.UpdateAsync(failed with { State = DownloadState.Failed, ErrorMessage = "x" }, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, (await this.downloads.ClearAsync([], CancellationToken.None).ConfigureAwait(true)).Error?.Code);
        Assert.Equal(1, await this.downloads.RetryFailedAsync(CancellationToken.None).ConfigureAwait(true));
        Assert.Equal(1, (await this.downloads.ClearAsync([DownloadState.Completed], CancellationToken.None).ConfigureAwait(true)).Value);

        Assert.Equal(ErrorCode.NotFound, (await this.downloads.GetAsync(done.Id, CancellationToken.None).ConfigureAwait(true)).Error?.Code);
        Assert.Equal(DownloadState.Queued, (await this.downloads.GetAsync(failed.Id, CancellationToken.None).ConfigureAwait(true)).Value.Job.State);
    }

    private Task KnowAsync(string id, string kind) =>
        this.nodes.UpsertAsync([new Node(TestData.Ref(id), NodeKind.Create(kind), id, null, null, [])], CancellationToken.None);

    private async Task<DownloadJob> EnqueueAsync(string id, string kind)
    {
        await this.KnowAsync(id, kind).ConfigureAwait(false);
        Outcome<DownloadJob> job = await this.downloads.EnqueueAsync(TestData.Ref(id), Image, null, CancellationToken.None).ConfigureAwait(false);
        return job.Value;
    }

    private async Task<DownloadState> WaitForFinalStateAsync(Guid id)
    {
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(10), TimeProvider.System);
        while (true)
        {
            DownloadState state = (await this.downloads.GetAsync(id, limit.Token).ConfigureAwait(false)).Value.Job.State;
            if (state is DownloadState.Completed or DownloadState.Failed or DownloadState.Canceled)
            {
                return state;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), limit.Token).ConfigureAwait(false);
        }
    }
}
