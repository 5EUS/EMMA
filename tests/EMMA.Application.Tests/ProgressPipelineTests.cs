using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.Infrastructure.InMemory;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.Application.Tests;

/// <summary>Tests for <see cref="ProgressPipeline"/>.</summary>
public sealed class ProgressPipelineTests
{
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly InMemoryNodeStore nodes = new();
    private readonly FakeAppSettings settings = new();
    private readonly ProgressPipeline pipeline;

    /// <summary>Initializes a new instance of the <see cref="ProgressPipelineTests"/> class.</summary>
    public ProgressPipelineTests() =>
        this.pipeline = new ProgressPipeline(new InMemoryProgressStore(this.nodes), this.settings, this.time);

    /// <summary>Progress is stamped by the injected clock and can be read back.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SetStampsWithTheClockAndStores()
    {
        Outcome<Progress> set = await this.pipeline.SetAsync(TestData.User, TestData.Ref("c1"), new ProgressLocator("page", 3, 20), false, CancellationToken.None).ConfigureAwait(true);
        Progress? read = await this.pipeline.GetAsync(TestData.User, TestData.Ref("c1"), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(this.time.GetUtcNow(), set.Value.UpdatedAt);
        Assert.Equal(set.Value, read);
    }

    /// <summary>Malformed locators are rejected and nothing is stored.</summary>
    /// <param name="unit">The unit.</param>
    /// <param name="value">The position.</param>
    /// <param name="total">The extent.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("Page", 1.0, 2.0)]
    [InlineData("", 1.0, 2.0)]
    [InlineData("page", -1.0, 2.0)]
    [InlineData("page", double.NaN, 2.0)]
    [InlineData("page", 5.0, 2.0)]
    [InlineData("page", 1.0, double.PositiveInfinity)]
    public async Task InvalidLocatorsAreRejected(string unit, double value, double total)
    {
        Outcome<Progress> set = await this.pipeline.SetAsync(TestData.User, TestData.Ref("c1"), new ProgressLocator(unit, value, total), false, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, set.Error?.Code);
        Assert.Null(await this.pipeline.GetAsync(TestData.User, TestData.Ref("c1"), CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>A locator without a total is valid; the unit is never interpreted.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UnknownUnitsAreAccepted()
    {
        Outcome<Progress> set = await this.pipeline.SetAsync(TestData.User, TestData.Ref("t1"), new ProgressLocator("paragraph", 12, null), true, CancellationToken.None).ConfigureAwait(true);

        Assert.True(set.IsSuccess);
        Assert.True(set.Value.Completed);
    }

    /// <summary>Continue returns the most recently updated progress within a container, however deep.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ContinueFindsTheLatestDescendant()
    {
        await this.SeedNodesAsync().ConfigureAwait(true);

        await this.pipeline.SetAsync(TestData.User, TestData.Ref("c1"), new ProgressLocator("page", 1, null), false, CancellationToken.None).ConfigureAwait(true);
        this.time.Advance(TimeSpan.FromMinutes(1));
        await this.pipeline.SetAsync(TestData.User, TestData.Ref("c2"), new ProgressLocator("page", 2, null), false, CancellationToken.None).ConfigureAwait(true);
        this.time.Advance(TimeSpan.FromMinutes(1));
        await this.pipeline.SetAsync(TestData.User, TestData.Ref("other"), new ProgressLocator("page", 9, null), false, CancellationToken.None).ConfigureAwait(true);

        Progress? latest = await this.pipeline.ContinueAsync(TestData.User, TestData.Ref("s1"), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(TestData.Ref("c2"), latest?.Node);
    }

    /// <summary>Recent progress is ordered newest first and the limit is clamped to at least one entry.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RecentIsNewestFirstAndClamped()
    {
        await this.pipeline.SetAsync(TestData.User, TestData.Ref("a"), new ProgressLocator("page", 1, null), false, CancellationToken.None).ConfigureAwait(true);
        this.time.Advance(TimeSpan.FromMinutes(1));
        await this.pipeline.SetAsync(TestData.User, TestData.Ref("b"), new ProgressLocator("page", 1, null), false, CancellationToken.None).ConfigureAwait(true);

        IReadOnlyList<Progress> all = await this.pipeline.RecentAsync(TestData.User, 10, CancellationToken.None).ConfigureAwait(true);
        IReadOnlyList<Progress> clamped = await this.pipeline.RecentAsync(TestData.User, 0, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal([TestData.Ref("b"), TestData.Ref("a")], all.Select(entry => entry.Node));
        Assert.Single(clamped);
    }

    /// <summary>Marking nodes complete keeps an existing position's unit and extent, and records a bare finish for the rest.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CompleteFinishesExistingAndNewNodes()
    {
        await this.pipeline.SetAsync(TestData.User, TestData.Ref("c1"), new ProgressLocator("page", 3, 20), false, CancellationToken.None).ConfigureAwait(true);
        this.time.Advance(TimeSpan.FromMinutes(1));

        IReadOnlyList<Progress> stored = await this.pipeline.CompleteAsync(TestData.User, [TestData.Ref("c1"), TestData.Ref("c2"), TestData.Ref("c1")], CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(2, stored.Count);
        Assert.Equal(new ProgressLocator("page", 20, 20), stored[0].Locator);
        Assert.Equal(new ProgressLocator(ProgressPipeline.FinishedUnit, 1, 1), stored[1].Locator);
        Assert.All(stored, entry => Assert.True(entry.Completed));
        Assert.All(stored, entry => Assert.Equal(this.time.GetUtcNow(), entry.UpdatedAt));
        Assert.Equal(stored[0], await this.pipeline.GetAsync(TestData.User, TestData.Ref("c1"), CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>Deleting a series removes its chapters' progress too; deleting what is not there removes nothing.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DeleteRemovesTheSubtreeAndClearRemovesEverything()
    {
        await this.SeedSeriesAsync().ConfigureAwait(true);
        await this.pipeline.SetAsync(TestData.User, TestData.Ref("other"), new ProgressLocator("page", 1, null), false, CancellationToken.None).ConfigureAwait(true);

        int removed = await this.pipeline.DeleteAsync(TestData.User, [TestData.Ref("s1"), TestData.Ref("s1")], CancellationToken.None).ConfigureAwait(true);
        int none = await this.pipeline.DeleteAsync(TestData.User, [TestData.Ref("s1")], CancellationToken.None).ConfigureAwait(true);
        int cleared = await this.pipeline.ClearAsync(TestData.User, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(2, removed);
        Assert.Equal(0, none);
        Assert.Equal(1, cleared);
    }

    /// <summary>The series view counts finished and unfinished chapters and reports the latest activity.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SeriesCountsAndListsChapters()
    {
        await this.SeedSeriesAsync().ConfigureAwait(true);

        SeriesProgress series = await this.pipeline.SeriesAsync(TestData.User, TestData.Ref("s1"), CancellationToken.None).ConfigureAwait(true);
        SeriesProgress empty = await this.pipeline.SeriesAsync(TestData.User, TestData.Ref("other"), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal((1, 1), (series.Completed, series.InProgress));
        Assert.Equal([TestData.Ref("c2"), TestData.Ref("c1")], series.Items.Select(entry => entry.Node));
        Assert.Equal(series.Items[0].UpdatedAt, series.LatestActivity);
        Assert.Equal((0, 0, null), (empty.Completed, empty.InProgress, empty.LatestActivity));
    }

    /// <summary>History groups chapters under their series and clamps the limit.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task HistoryGroupsBySeries()
    {
        await this.SeedSeriesAsync().ConfigureAwait(true);

        IReadOnlyList<ProgressGroup> groups = await this.pipeline.HistoryAsync(TestData.User, 0, CancellationToken.None).ConfigureAwait(true);

        ProgressGroup group = Assert.Single(groups);
        Assert.Equal(TestData.Ref("s1"), group.Series);
        Assert.Equal(TestData.Ref("c2"), group.Latest.Node);
        Assert.Equal((1, 1), (group.Completed, group.InProgress));
    }

    /// <summary>Retention defaults to keeping everything, rejects nonsense, and deletes older history when set and when progress is recorded.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RetentionDeletesOldHistory()
    {
        Assert.Equal(0, await this.pipeline.GetRetentionAsync(TestData.User, CancellationToken.None).ConfigureAwait(true));
        Assert.Equal(ErrorCode.InvalidArgument, (await this.pipeline.SetRetentionAsync(TestData.User, -1, CancellationToken.None).ConfigureAwait(true)).Error?.Code);
        Assert.Equal(ErrorCode.InvalidArgument, (await this.pipeline.SetRetentionAsync(TestData.User, ProgressPipeline.MaxRetentionDays + 1, CancellationToken.None).ConfigureAwait(true)).Error?.Code);

        await this.pipeline.SetAsync(TestData.User, TestData.Ref("old"), new ProgressLocator("page", 1, null), false, CancellationToken.None).ConfigureAwait(true);
        this.time.Advance(TimeSpan.FromDays(10));
        await this.pipeline.SetAsync(TestData.User, TestData.Ref("mid"), new ProgressLocator("page", 1, null), false, CancellationToken.None).ConfigureAwait(true);
        this.time.Advance(TimeSpan.FromDays(10));

        Outcome<HistoryRetention> set = await this.pipeline.SetRetentionAsync(TestData.User, 15, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(new HistoryRetention(15, 1), set.Value);
        Assert.Equal(15, await this.pipeline.GetRetentionAsync(TestData.User, CancellationToken.None).ConfigureAwait(true));
        Assert.Equal([TestData.Ref("mid")], (await this.pipeline.RecentAsync(TestData.User, 10, CancellationToken.None).ConfigureAwait(true)).Select(entry => entry.Node));

        this.time.Advance(TimeSpan.FromDays(10));
        await this.pipeline.SetAsync(TestData.User, TestData.Ref("new"), new ProgressLocator("page", 1, null), false, CancellationToken.None).ConfigureAwait(true);
        Assert.Equal([TestData.Ref("new")], (await this.pipeline.RecentAsync(TestData.User, 10, CancellationToken.None).ConfigureAwait(true)).Select(entry => entry.Node));

        Outcome<HistoryRetention> forever = await this.pipeline.SetRetentionAsync(TestData.User, 0, CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(new HistoryRetention(0, 0), forever.Value);
        Assert.Equal(0, await this.pipeline.GetRetentionAsync(TestData.User, CancellationToken.None).ConfigureAwait(true));
    }

    private Task SeedNodesAsync()
    {
        NodeKind kind = NodeKind.Create("series");
        return this.nodes.UpsertAsync(
            [
                new Node(TestData.Ref("s1"), kind, "S", null, null, []),
                new Node(TestData.Ref("c1"), kind, "C1", TestData.Ref("s1"), null, []),
                new Node(TestData.Ref("c2"), kind, "C2", TestData.Ref("s1"), null, []),
                new Node(TestData.Ref("other"), kind, "O", null, null, []),
            ],
            CancellationToken.None);
    }

    private async Task SeedSeriesAsync()
    {
        await this.SeedNodesAsync().ConfigureAwait(false);
        await this.pipeline.SetAsync(TestData.User, TestData.Ref("c1"), new ProgressLocator("page", 20, 20), true, CancellationToken.None).ConfigureAwait(false);
        this.time.Advance(TimeSpan.FromMinutes(1));
        await this.pipeline.SetAsync(TestData.User, TestData.Ref("c2"), new ProgressLocator("page", 2, 20), false, CancellationToken.None).ConfigureAwait(false);
    }
}
