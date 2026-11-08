using EMMA.Domain;
using EMMA.TestSupport;

using Microsoft.Data.Sqlite;

using Xunit;

namespace EMMA.Storage.Tests;

/// <summary>Tests for <see cref="SqliteProgressStore"/>.</summary>
public sealed class SqliteProgressStoreTests : StorageTestBase
{
    private static readonly UserId Bob = UserId.Create("bob");

    private SqliteProgressStore Store => new(this.Options);

    private SqliteNodeStore Nodes => new(this.Options, this.Time);

    /// <summary>Progress comes back as stored, with and without a total.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RoundTrips()
    {
        Progress paged = Make("c1", "page", 3, 20, completed: false);
        Progress open = Make("t1", "paragraph", 12, null, completed: true);

        await this.Store.UpsertAsync(paged, Ct);
        await this.Store.UpsertAsync(open, Ct);

        Assert.Equal(paged, await this.Store.GetAsync(TestData.User, paged.Node, Ct));
        Assert.Equal(open, await this.Store.GetAsync(TestData.User, open.Node, Ct));
    }

    /// <summary>Writing again replaces the position; there is one row per user and node.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UpsertOverwrites()
    {
        await this.Store.UpsertAsync(Make("c1", "page", 1, 20, completed: false), Ct);
        await this.Store.UpsertAsync(Make("c1", "page", 20, 20, completed: true), Ct);

        Progress? loaded = await this.Store.GetAsync(TestData.User, TestData.Ref("c1"), Ct);

        Assert.Equal(20, loaded!.Locator.Value);
        Assert.True(loaded.Completed);
        Assert.Equal(1L, await this.ScalarAsync("SELECT COUNT(*) FROM progress"));
    }

    /// <summary>Unknown progress is null, and one user never sees another's.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task MissingAndOtherUsersAreNull()
    {
        await this.Store.UpsertAsync(Make("c1", "page", 1, null, completed: false), Ct);

        Assert.Null(await this.Store.GetAsync(TestData.User, TestData.Ref("other"), Ct));
        Assert.Null(await this.Store.GetAsync(Bob, TestData.Ref("c1"), Ct));
    }

    /// <summary>Continue finds the most recent progress anywhere beneath a container, at any depth, including the container itself.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LatestWithinWalksTheSubtree()
    {
        await this.Nodes.UpsertAsync(
            [MakeNode("series"), MakeNode("season", "series"), MakeNode("ep1", "season"), MakeNode("ep2", "season"), MakeNode("elsewhere")],
            Ct);
        await this.Store.UpsertAsync(Make("ep1", "ms", 100, null, completed: false, minute: 1), Ct);
        await this.Store.UpsertAsync(Make("ep2", "ms", 200, null, completed: false, minute: 2), Ct);
        await this.Store.UpsertAsync(Make("elsewhere", "ms", 999, null, completed: false, minute: 3), Ct);

        Progress? latest = await this.Store.GetLatestWithinAsync(TestData.User, TestData.Ref("series"), Ct);
        Progress? onSeason = await this.Store.GetLatestWithinAsync(TestData.User, TestData.Ref("season"), Ct);
        Progress? onEpisode = await this.Store.GetLatestWithinAsync(TestData.User, TestData.Ref("ep1"), Ct);

        Assert.Equal(TestData.Ref("ep2"), latest!.Node);
        Assert.Equal(TestData.Ref("ep2"), onSeason!.Node);
        Assert.Equal(TestData.Ref("ep1"), onEpisode!.Node);
    }

    /// <summary>Continue ignores other users, and other plugins that happen to reuse the same local ids.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LatestWithinIgnoresOtherUsersAndPlugins()
    {
        NodeRef foreign = new(TestData.OtherPlugin, LocalNodeId.Create("ep1"));
        await this.Nodes.UpsertAsync([MakeNode("series"), MakeNode("ep1", "series")], Ct);
        await this.Store.UpsertAsync(Make("ep1", "ms", 1, null, completed: false) with { User = Bob }, Ct);
        await this.Store.UpsertAsync(Make("ep1", "ms", 1, null, completed: false) with { Node = foreign }, Ct);

        Assert.Null(await this.Store.GetLatestWithinAsync(TestData.User, TestData.Ref("series"), Ct));
    }

    /// <summary>A parent cycle in bad data ends the walk instead of looping forever.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LatestWithinSurvivesAParentCycle()
    {
        await this.Nodes.UpsertAsync([MakeNode("a", "b"), MakeNode("b", "a")], Ct);
        await this.Store.UpsertAsync(Make("b", "page", 1, null, completed: false), Ct);

        Progress? latest = await this.Store.GetLatestWithinAsync(TestData.User, TestData.Ref("a"), Ct);

        Assert.Equal(TestData.Ref("b"), latest!.Node);
    }

    /// <summary>Recent progress is newest first, limited, and private to the user.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RecentIsNewestFirstLimitedAndPrivate()
    {
        await this.Store.UpsertAsync(Make("a", "page", 1, null, completed: false, minute: 1), Ct);
        await this.Store.UpsertAsync(Make("b", "page", 1, null, completed: false, minute: 3), Ct);
        await this.Store.UpsertAsync(Make("c", "page", 1, null, completed: false, minute: 2), Ct);
        await this.Store.UpsertAsync(Make("d", "page", 1, null, completed: false, minute: 9) with { User = Bob }, Ct);

        IReadOnlyList<Progress> all = await this.Store.ListRecentAsync(TestData.User, 10, Ct);
        IReadOnlyList<Progress> top = await this.Store.ListRecentAsync(TestData.User, 2, Ct);

        Assert.Equal([TestData.Ref("b"), TestData.Ref("c"), TestData.Ref("a")], all.Select(entry => entry.Node));
        Assert.Equal(2, top.Count);
        Assert.Empty(await this.Store.ListRecentAsync(TestData.User, 0, Ct));
    }

    /// <summary>The database itself refuses nonsense, so a bug above cannot store it.</summary>
    /// <param name="value">The position.</param>
    /// <param name="total">The extent.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData(-1.0, null)]
    [InlineData(5.0, 2.0)]
    public Task TheDatabaseRejectsInvalidLocators(double value, double? total) =>
        Assert.ThrowsAsync<SqliteException>(() => this.Store.UpsertAsync(Make("c1", "page", value, total, completed: false), Ct));

    private static Progress Make(string node, string unit, double value, double? total, bool completed, int minute = 0) => new(
        TestData.User,
        TestData.Ref(node),
        new ProgressLocator(unit, value, total),
        completed,
        new DateTimeOffset(2026, 1, 1, 0, minute, 0, TimeSpan.Zero));
}
