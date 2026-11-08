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

    /// <summary>The subtree listing holds the node and everything beneath it, newest first, and honours the limit.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ListWithinListsTheSubtreeNewestFirst()
    {
        await this.Nodes.UpsertAsync([MakeNode("series"), MakeNode("ep1", "series"), MakeNode("ep2", "series"), MakeNode("elsewhere")], Ct);
        await this.Store.UpsertAsync(Make("ep1", "ms", 1, null, completed: true, minute: 1), Ct);
        await this.Store.UpsertAsync(Make("ep2", "ms", 1, null, completed: false, minute: 2), Ct);
        await this.Store.UpsertAsync(Make("elsewhere", "ms", 1, null, completed: false, minute: 3), Ct);
        await this.Store.UpsertAsync(Make("ep1", "ms", 1, null, completed: false, minute: 4) with { User = Bob }, Ct);

        IReadOnlyList<Progress> within = await this.Store.ListWithinAsync(TestData.User, TestData.Ref("series"), 10, Ct);
        IReadOnlyList<Progress> top = await this.Store.ListWithinAsync(TestData.User, TestData.Ref("series"), 1, Ct);

        Assert.Equal([TestData.Ref("ep2"), TestData.Ref("ep1")], within.Select(entry => entry.Node));
        Assert.Single(top);
    }

    /// <summary>Groups are keyed by the topmost cached ancestor, count finished and unfinished entries, and carry the latest one.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ListGroupsGroupsBySeries()
    {
        await this.Nodes.UpsertAsync(
            [
                MakeNode("show"),
                MakeNode("season", "show"),
                MakeNode("ep1", "season"),
                MakeNode("ep2", "season"),
                MakeNode("manga"),
                MakeNode("c1", "manga"),
                MakeNode("orphan", "gone"),
            ],
            Ct);
        await this.Store.UpsertAsync(Make("ep1", "ms", 1, null, completed: true, minute: 1), Ct);
        await this.Store.UpsertAsync(Make("ep2", "ms", 5, null, completed: false, minute: 5), Ct);
        await this.Store.UpsertAsync(Make("c1", "page", 3, 9, completed: false, minute: 3), Ct);
        await this.Store.UpsertAsync(Make("orphan", "page", 1, null, completed: true, minute: 2), Ct);
        await this.Store.UpsertAsync(Make("uncached", "page", 1, null, completed: false, minute: 0), Ct);
        await this.Store.UpsertAsync(Make("ep1", "ms", 1, null, completed: true, minute: 9) with { User = Bob }, Ct);

        IReadOnlyList<ProgressGroup> groups = await this.Store.ListGroupsAsync(TestData.User, 10, Ct);
        IReadOnlyList<ProgressGroup> top = await this.Store.ListGroupsAsync(TestData.User, 1, Ct);

        Assert.Equal(
            [TestData.Ref("show"), TestData.Ref("manga"), TestData.Ref("orphan"), TestData.Ref("uncached")],
            groups.Select(group => group.Series));
        Assert.Equal(TestData.Ref("ep2"), groups[0].Latest.Node);
        Assert.Equal((1, 1), (groups[0].Completed, groups[0].InProgress));
        Assert.Equal((0, 1), (groups[1].Completed, groups[1].InProgress));
        Assert.Equal((1, 0), (groups[2].Completed, groups[2].InProgress));
        Assert.Single(top);
    }

    /// <summary>A parent cycle in bad data does not hang the grouping.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ListGroupsSurvivesAParentCycle()
    {
        await this.Nodes.UpsertAsync([MakeNode("a", "b"), MakeNode("b", "a")], Ct);
        await this.Store.UpsertAsync(Make("b", "page", 1, null, completed: false), Ct);

        Assert.Single(await this.Store.ListGroupsAsync(TestData.User, 10, Ct));
    }

    /// <summary>Deleting a subtree removes the node and its descendants for that user only.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DeleteWithinRemovesTheSubtreeForOneUser()
    {
        await this.Nodes.UpsertAsync([MakeNode("series"), MakeNode("ep1", "series"), MakeNode("elsewhere")], Ct);
        await this.Store.UpsertAsync(Make("series", "page", 1, null, completed: false), Ct);
        await this.Store.UpsertAsync(Make("ep1", "page", 1, null, completed: false), Ct);
        await this.Store.UpsertAsync(Make("elsewhere", "page", 1, null, completed: false), Ct);
        await this.Store.UpsertAsync(Make("ep1", "page", 1, null, completed: false) with { User = Bob }, Ct);

        int removed = await this.Store.DeleteWithinAsync(TestData.User, TestData.Ref("series"), Ct);

        Assert.Equal(2, removed);
        Assert.Equal([TestData.Ref("elsewhere")], (await this.Store.ListRecentAsync(TestData.User, 10, Ct)).Select(entry => entry.Node));
        Assert.Single(await this.Store.ListRecentAsync(Bob, 10, Ct));
        Assert.Equal(0, await this.Store.DeleteWithinAsync(TestData.User, TestData.Ref("series"), Ct));
    }

    /// <summary>Clearing removes one user's progress and nobody else's.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ClearRemovesOneUsersProgress()
    {
        await this.Store.UpsertAsync(Make("a", "page", 1, null, completed: false), Ct);
        await this.Store.UpsertAsync(Make("b", "page", 1, null, completed: false), Ct);
        await this.Store.UpsertAsync(Make("a", "page", 1, null, completed: false) with { User = Bob }, Ct);

        Assert.Equal(2, await this.Store.ClearAsync(TestData.User, Ct));
        Assert.Empty(await this.Store.ListRecentAsync(TestData.User, 10, Ct));
        Assert.Single(await this.Store.ListRecentAsync(Bob, 10, Ct));
    }

    /// <summary>Deleting before a moment removes only older entries.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DeleteBeforeRemovesOlderEntries()
    {
        await this.Store.UpsertAsync(Make("old", "page", 1, null, completed: false, minute: 1), Ct);
        await this.Store.UpsertAsync(Make("edge", "page", 1, null, completed: false, minute: 5), Ct);
        await this.Store.UpsertAsync(Make("new", "page", 1, null, completed: false, minute: 9), Ct);
        await this.Store.UpsertAsync(Make("old", "page", 1, null, completed: false, minute: 1) with { User = Bob }, Ct);

        int removed = await this.Store.DeleteBeforeAsync(TestData.User, new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero), Ct);

        Assert.Equal(1, removed);
        Assert.Equal([TestData.Ref("new"), TestData.Ref("edge")], (await this.Store.ListRecentAsync(TestData.User, 10, Ct)).Select(entry => entry.Node));
        Assert.Single(await this.Store.ListRecentAsync(Bob, 10, Ct));
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
