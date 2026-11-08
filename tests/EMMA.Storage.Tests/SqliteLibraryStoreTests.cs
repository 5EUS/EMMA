using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Storage.Tests;

/// <summary>Tests for <see cref="SqliteLibraryStore"/>.</summary>
public sealed class SqliteLibraryStoreTests : StorageTestBase
{
    private static readonly UserId Bob = UserId.Create("bob");

    private SqliteLibraryStore Store => new(this.Options);

    /// <summary>The library lists most recently added first.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ListsNewestFirst()
    {
        await this.Store.AddAsync(Entry("a", minute: 1), Ct);
        await this.Store.AddAsync(Entry("b", minute: 3), Ct);
        await this.Store.AddAsync(Entry("c", minute: 2), Ct);

        IReadOnlyList<LibraryEntry> entries = await this.Store.ListAsync(TestData.User, Ct);

        Assert.Equal([TestData.Ref("b"), TestData.Ref("c"), TestData.Ref("a")], entries.Select(entry => entry.Node));
    }

    /// <summary>Adding a node twice keeps the original date and a single entry.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AddingTwiceKeepsTheOriginalDate()
    {
        await this.Store.AddAsync(Entry("a", minute: 1), Ct);
        await this.Store.AddAsync(Entry("a", minute: 30), Ct);

        IReadOnlyList<LibraryEntry> entries = await this.Store.ListAsync(TestData.User, Ct);

        Assert.Single(entries);
        Assert.Equal(Entry("a", minute: 1), entries[0]);
    }

    /// <summary>Removing reports whether anything was removed.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RemoveReportsWhetherAnEntryExisted()
    {
        await this.Store.AddAsync(Entry("a"), Ct);

        Assert.True(await this.Store.RemoveAsync(TestData.User, TestData.Ref("a"), Ct));
        Assert.False(await this.Store.RemoveAsync(TestData.User, TestData.Ref("a"), Ct));
        Assert.Empty(await this.Store.ListAsync(TestData.User, Ct));
    }

    /// <summary>Libraries are private per user, and the same local id under two plugins is two entries.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LibrariesArePerUserAndPerPlugin()
    {
        NodeRef other = new(TestData.OtherPlugin, LocalNodeId.Create("a"));
        await this.Store.AddAsync(Entry("a"), Ct);
        await this.Store.AddAsync(Entry("a") with { Node = other }, Ct);
        await this.Store.AddAsync(Entry("a") with { User = Bob }, Ct);

        Assert.Equal(2, (await this.Store.ListAsync(TestData.User, Ct)).Count);
        Assert.Single(await this.Store.ListAsync(Bob, Ct));
        Assert.False(await this.Store.RemoveAsync(Bob, other, Ct));
    }

    /// <summary>A library entry survives clearing the node cache: it holds only a reference.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SurvivesClearingTheNodeCache()
    {
        await new SqliteNodeStore(this.Options, this.Time).UpsertAsync([MakeNode("a")], Ct);
        await this.Store.AddAsync(Entry("a"), Ct);

        await this.ScalarAsync("DELETE FROM nodes");

        Assert.Single(await this.Store.ListAsync(TestData.User, Ct));
    }

    private static LibraryEntry Entry(string node, int minute = 0) => new(
        TestData.User,
        TestData.Ref(node),
        new DateTimeOffset(2026, 1, 1, 0, minute, 0, TimeSpan.Zero));
}
