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
        DownloadJob queued = Job();
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

    private static DateTimeOffset At(int minute) => new(2026, 1, 1, 0, minute, 0, TimeSpan.Zero);

    private static DownloadJob Job(int minute = 0) => new(
        Guid.NewGuid(),
        TestData.Ref("series"),
        AssetRole.Create("image"),
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
