using EMMA.Domain;
using EMMA.TestSupport;

using Microsoft.Data.Sqlite;

using Xunit;

namespace EMMA.Storage.Tests;

/// <summary>Tests for <see cref="SqliteDownloadStore"/>.</summary>
public sealed class SqliteDownloadStoreTests : StorageTestBase
{
    private SqliteDownloadStore Store => new(this.Options);

    /// <summary>A job, including its optional timestamps and message, comes back exactly as stored.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RoundTrips()
    {
        DownloadJob queued = Job() with { Hint = "1080p" };
        DownloadJob failed = Job() with
        {
            State = DownloadState.Failed,
            ErrorMessage = "disk full",
            StartedAt = At(1),
            CompletedAt = At(2),
            ItemsCompleted = 3,
            ItemsTotal = 10,
            BytesDownloaded = 4_000_000_000,
        };

        await this.Store.CreateAsync(queued, Ct);
        await this.Store.CreateAsync(failed, Ct);

        Assert.Equal(queued, await this.Store.GetAsync(queued.Id, Ct));
        Assert.Equal(failed, await this.Store.GetAsync(failed.Id, Ct));
    }

    /// <summary>An unknown job is null.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UnknownJobIsNull() => Assert.Null(await this.Store.GetAsync(Guid.NewGuid(), Ct));

    /// <summary>Listing is newest first, limited, and can be filtered by any set of states.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ListsNewestFirstWithOptionalStateFilter()
    {
        DownloadJob oldest = Job(minute: 1);
        DownloadJob running = Job(minute: 2) with { State = DownloadState.Running };
        DownloadJob newest = Job(minute: 3) with { State = DownloadState.Completed };
        foreach (DownloadJob job in new[] { oldest, running, newest })
        {
            await this.Store.CreateAsync(job, Ct);
        }

        Assert.Equal([newest.Id, running.Id, oldest.Id], (await this.Store.ListAsync(null, 10, Ct)).Select(job => job.Id));
        Assert.Equal([running.Id, oldest.Id], (await this.Store.ListAsync([DownloadState.Queued, DownloadState.Running], 10, Ct)).Select(job => job.Id));
        Assert.Single(await this.Store.ListAsync(null, 1, Ct));
        Assert.Empty(await this.Store.ListAsync([], 10, Ct));
    }

    /// <summary>Update replaces the changing fields and reports whether the job existed; delete likewise.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UpdateAndDeleteReportWhetherTheJobExisted()
    {
        DownloadJob job = Job();
        await this.Store.CreateAsync(job, Ct);

        DownloadJob changed = job with { State = DownloadState.Running, ItemsCompleted = 2, ItemsTotal = 5, StartedAt = At(1), UpdatedAt = At(1) };
        Assert.True(await this.Store.UpdateAsync(changed, Ct));
        Assert.Equal(changed, await this.Store.GetAsync(job.Id, Ct));
        Assert.False(await this.Store.UpdateAsync(Job(), Ct));

        Assert.True(await this.Store.DeleteAsync(job.Id, Ct));
        Assert.False(await this.Store.DeleteAsync(job.Id, Ct));
        Assert.Null(await this.Store.GetAsync(job.Id, Ct));
    }

    /// <summary>The database refuses an unknown state or a negative counter.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TheDatabaseRejectsInvalidValues()
    {
        await Assert.ThrowsAsync<SqliteException>(() => this.Store.CreateAsync(Job() with { State = (DownloadState)9 }, Ct));
        await Assert.ThrowsAsync<SqliteException>(() => this.Store.CreateAsync(Job() with { BytesDownloaded = -1 }, Ct));
    }

    /// <summary>The most recent job for a node and role is found; another role or node is not.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task FindsTheLatestJobForANodeAndRole()
    {
        DownloadJob older = Job(minute: 1);
        DownloadJob newer = Job(minute: 2);
        DownloadJob otherRole = Job(minute: 3) with { Role = AssetRole.Create("text") };
        foreach (DownloadJob job in new[] { older, newer, otherRole })
        {
            await this.Store.CreateAsync(job, Ct);
        }

        Assert.Equal(newer.Id, (await this.Store.FindAsync(TestData.Ref("series"), AssetRole.Create("image"), Ct))?.Id);
        Assert.Equal(otherRole.Id, (await this.Store.FindAsync(TestData.Ref("series"), AssetRole.Create("text"), Ct))?.Id);
        Assert.Null(await this.Store.FindAsync(TestData.Ref("other"), AssetRole.Create("image"), Ct));
    }

    /// <summary>A transition is applied only when the stored job is still in the state the caller saw.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TransitionsOnlyFromTheExpectedState()
    {
        DownloadJob job = Job();
        await this.Store.CreateAsync(job, Ct);

        DownloadJob running = job with { State = DownloadState.Running, StartedAt = At(1) };
        DownloadJob paused = job with { State = DownloadState.Paused };

        Assert.False(await this.Store.TryTransitionAsync(running, DownloadState.Paused, Ct));
        Assert.Equal(DownloadState.Queued, (await this.Store.GetAsync(job.Id, Ct))!.State);
        Assert.True(await this.Store.TryTransitionAsync(running, DownloadState.Queued, Ct));
        Assert.False(await this.Store.TryTransitionAsync(running with { ItemsCompleted = 1 }, DownloadState.Queued, Ct));
        Assert.True(await this.Store.TryTransitionAsync(paused, DownloadState.Running, Ct));
        Assert.Equal(DownloadState.Paused, (await this.Store.GetAsync(job.Id, Ct))!.State);
        Assert.False(await this.Store.TryTransitionAsync(paused with { Id = Guid.NewGuid() }, DownloadState.Paused, Ct));
    }

    /// <summary>A job's leaves come back in position order, and updating one records the file, type, size and error.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task StoresLeavesInOrderAndUpdatesThem()
    {
        DownloadJob job = Job();
        await this.Store.CreateAsync(job, Ct);
        await this.Store.AddItemsAsync([Item(job, 1, "p2"), Item(job, 0, "p1")], Ct);

        IReadOnlyList<DownloadItem> listed = await this.Store.ListItemsAsync(job.Id, Ct);
        Assert.Equal([0, 1], listed.Select(item => item.Position));
        Assert.All(listed, item => Assert.False(item.IsStored));

        DownloadItem stored = listed[0] with { File = "abc123", Mime = "image/png", Bytes = 42 };
        DownloadItem failed = listed[1] with { ErrorMessage = "gone" };
        Assert.True(await this.Store.UpdateItemAsync(stored, Ct));
        Assert.True(await this.Store.UpdateItemAsync(failed, Ct));
        Assert.False(await this.Store.UpdateItemAsync(stored with { Position = 9 }, Ct));

        Assert.Equal([stored, failed], await this.Store.ListItemsAsync(job.Id, Ct));
        Assert.Equal(stored, await this.Store.FindStoredAsync(TestData.Ref("p1"), AssetRole.Create("image"), Ct));
        Assert.Null(await this.Store.FindStoredAsync(TestData.Ref("p2"), AssetRole.Create("image"), Ct));
    }

    /// <summary>Two leaves of one job cannot take the same position, and a leaf needs a job.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TheDatabaseRejectsDuplicatePositionsAndOrphanLeaves()
    {
        DownloadJob job = Job();
        await this.Store.CreateAsync(job, Ct);
        await this.Store.AddItemsAsync([Item(job, 0, "p1")], Ct);

        await Assert.ThrowsAsync<SqliteException>(() => this.Store.AddItemsAsync([Item(job, 0, "p2")], Ct));
        await Assert.ThrowsAsync<SqliteException>(() => this.Store.AddItemsAsync([Item(Job(), 0, "p3")], Ct));
        Assert.Single(await this.Store.ListItemsAsync(job.Id, Ct));
    }

    /// <summary>Deleting a job deletes its leaves, and only files no other job names are reported as free.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DeletingAJobDeletesItsLeavesAndReportsOnlyItsOwnFiles()
    {
        DownloadJob chapter = Job(minute: 1);
        DownloadJob series = Job(minute: 2) with { Node = TestData.Ref("whole") };
        await this.Store.CreateAsync(chapter, Ct);
        await this.Store.CreateAsync(series, Ct);
        await this.Store.AddItemsAsync([Item(chapter, 0, "p1") with { File = "aa", Mime = "image/png", Bytes = 1 }], Ct);
        await this.Store.AddItemsAsync(
            [
                Item(series, 0, "p1") with { File = "aa", Mime = "image/png", Bytes = 1 },
                Item(series, 1, "p2") with { File = "bb", Mime = "image/png", Bytes = 1 },
                Item(series, 2, "p3"),
            ],
            Ct);

        Assert.Equal(["bb"], await this.Store.ListExclusiveFilesAsync(series.Id, Ct));
        Assert.Empty(await this.Store.ListExclusiveFilesAsync(chapter.Id, Ct));

        Assert.True(await this.Store.DeleteAsync(series.Id, Ct));
        Assert.Empty(await this.Store.ListItemsAsync(series.Id, Ct));
        Assert.Single(await this.Store.ListItemsAsync(chapter.Id, Ct));
        Assert.Equal(["aa"], await this.Store.ListExclusiveFilesAsync(chapter.Id, Ct));
    }

    private static DateTimeOffset At(int minute) => new(2026, 1, 1, 0, minute, 0, TimeSpan.Zero);

    private static DownloadItem Item(DownloadJob job, int position, string leaf) =>
        new(job.Id, position, TestData.Ref(leaf), AssetRole.Create("image"), null, null, 0, null);

    private static DownloadJob Job(int minute = 0) => new(
        Guid.NewGuid(),
        TestData.Ref("series"),
        AssetRole.Create("image"),
        null,
        DownloadState.Queued,
        0,
        0,
        0,
        null,
        At(minute),
        At(minute),
        null,
        null);
}
