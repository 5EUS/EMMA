using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Storage.Tests;

/// <summary>Tests for <see cref="SqliteLibraryStore"/>.</summary>
public sealed class SqliteLibraryStoreTests : StorageTestBase
{
    private static readonly UserId Bob = UserId.Create("bob");
    private static readonly LibraryName Work = LibraryName.Create("Work");

    private SqliteLibraryStore Store => new(this.Options, this.Time);

    /// <summary>The library lists most recently added first.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ListsNewestFirst()
    {
        await this.Store.AddAsync(Entry("a", minute: 1), Ct);
        await this.Store.AddAsync(Entry("b", minute: 3), Ct);
        await this.Store.AddAsync(Entry("c", minute: 2), Ct);

        IReadOnlyList<LibraryEntry> entries = await this.Store.ListAsync(TestData.User, null, Ct);

        Assert.Equal([TestData.Ref("b"), TestData.Ref("c"), TestData.Ref("a")], entries.Select(entry => entry.Node));
    }

    /// <summary>Adding a node twice keeps the original date and a single entry.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AddingTwiceKeepsTheOriginalDate()
    {
        Assert.True(await this.Store.AddAsync(Entry("a", minute: 1), Ct));
        Assert.False(await this.Store.AddAsync(Entry("a", minute: 30), Ct));

        IReadOnlyList<LibraryEntry> entries = await this.Store.ListAsync(TestData.User, null, Ct);

        Assert.Single(entries);
        Assert.Equal(Entry("a", minute: 1), entries[0]);
    }

    /// <summary>A node already saved in one library is not added to a second one.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ANodeIsInAtMostOneLibrary()
    {
        await this.Store.CreateLibraryAsync(TestData.User, Work, this.Time.GetUtcNow(), Ct);
        await this.Store.AddAsync(Entry("a"), Ct);

        Assert.False(await this.Store.AddAsync(Entry("a") with { Library = Work }, Ct));

        Assert.Equal(LibraryName.Default, (await this.Store.ListAsync(TestData.User, null, Ct)).Single().Library);
    }

    /// <summary>Removing reports whether anything was removed.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RemoveReportsWhetherAnEntryExisted()
    {
        await this.Store.AddAsync(Entry("a"), Ct);

        Assert.True(await this.Store.RemoveAsync(TestData.User, TestData.Ref("a"), Ct));
        Assert.False(await this.Store.RemoveAsync(TestData.User, TestData.Ref("a"), Ct));
        Assert.Empty(await this.Store.ListAsync(TestData.User, null, Ct));
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

        Assert.Equal(2, (await this.Store.ListAsync(TestData.User, null, Ct)).Count);
        Assert.Single(await this.Store.ListAsync(Bob, null, Ct));
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

        Assert.Single(await this.Store.ListAsync(TestData.User, null, Ct));
    }

    /// <summary>The default library exists from the start, is listed first, and holds what is added without a name.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TheDefaultLibraryExistsOnDemand()
    {
        await this.Store.CreateLibraryAsync(TestData.User, LibraryName.Create("Archive"), this.Time.GetUtcNow(), Ct);
        await this.Store.CreateLibraryAsync(TestData.User, Work, this.Time.GetUtcNow(), Ct);
        await this.Store.AddAsync(Entry("a"), Ct);

        IReadOnlyList<LibrarySummary> libraries = await this.Store.ListLibrariesAsync(TestData.User, Ct);
        IReadOnlyList<LibrarySummary> bobs = await this.Store.ListLibrariesAsync(Bob, Ct);

        Assert.Equal(["Default", "Archive", "Work"], libraries.Select(library => library.Name.Value));
        Assert.Equal([1, 0, 0], libraries.Select(library => library.Count));
        Assert.Equal(["Default"], bobs.Select(library => library.Name.Value));
    }

    /// <summary>A library name is unique ignoring case, and the casing it was created with is kept.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task NamesAreCaseInsensitive()
    {
        Assert.True(await this.Store.CreateLibraryAsync(TestData.User, Work, this.Time.GetUtcNow(), Ct));
        Assert.False(await this.Store.CreateLibraryAsync(TestData.User, LibraryName.Create("WORK"), this.Time.GetUtcNow(), Ct));
        Assert.True(await this.Store.CreateLibraryAsync(Bob, LibraryName.Create("work"), this.Time.GetUtcNow(), Ct));

        await this.Store.AddAsync(Entry("a") with { Library = LibraryName.Create("work") }, Ct);

        Assert.Equal(Work, (await this.Store.ListAsync(TestData.User, LibraryName.Create("WoRk"), Ct)).Single().Library);
        Assert.Contains(LibraryName.Create("Default"), (await this.Store.ListLibrariesAsync(TestData.User, Ct)).Select(library => library.Name));
    }

    /// <summary>Adding to a library that does not exist adds nothing.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AddingToAMissingLibraryAddsNothing()
    {
        Assert.False(await this.Store.AddAsync(Entry("a") with { Library = Work }, Ct));
        Assert.Empty(await this.Store.ListAsync(TestData.User, null, Ct));
    }

    /// <summary>Listing one library lists only its entries.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ListsOneLibrary()
    {
        await this.Store.CreateLibraryAsync(TestData.User, Work, this.Time.GetUtcNow(), Ct);
        await this.Store.AddAsync(Entry("a"), Ct);
        await this.Store.AddAsync(Entry("b") with { Library = Work }, Ct);

        Assert.Equal([TestData.Ref("b")], (await this.Store.ListAsync(TestData.User, Work, Ct)).Select(entry => entry.Node));
        Assert.Equal([TestData.Ref("a")], (await this.Store.ListAsync(TestData.User, LibraryName.Default, Ct)).Select(entry => entry.Node));
        Assert.Equal(2, (await this.Store.ListAsync(TestData.User, null, Ct)).Count);
    }

    /// <summary>Deleting a library deletes its entries and nothing else; deleting it twice reports it was gone.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DeletingALibraryDeletesItsEntries()
    {
        await this.Store.CreateLibraryAsync(TestData.User, Work, this.Time.GetUtcNow(), Ct);
        await this.Store.AddAsync(Entry("a"), Ct);
        await this.Store.AddAsync(Entry("b") with { Library = Work }, Ct);
        await this.Store.AddAsync(Entry("b") with { Library = LibraryName.Default, User = Bob }, Ct);

        Assert.True(await this.Store.DeleteLibraryAsync(TestData.User, LibraryName.Create("work"), Ct));
        Assert.False(await this.Store.DeleteLibraryAsync(TestData.User, Work, Ct));

        Assert.Equal([TestData.Ref("a")], (await this.Store.ListAsync(TestData.User, null, Ct)).Select(entry => entry.Node));
        Assert.Single(await this.Store.ListAsync(Bob, null, Ct));
    }

    /// <summary>Moving changes the library and keeps the date; a missing node or library moves nothing.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task MovingKeepsTheDate()
    {
        await this.Store.CreateLibraryAsync(TestData.User, Work, this.Time.GetUtcNow(), Ct);
        await this.Store.AddAsync(Entry("a", minute: 4), Ct);

        Assert.True(await this.Store.MoveAsync(TestData.User, TestData.Ref("a"), LibraryName.Create("WORK"), Ct));
        Assert.False(await this.Store.MoveAsync(TestData.User, TestData.Ref("a"), LibraryName.Create("Nowhere"), Ct));
        Assert.False(await this.Store.MoveAsync(TestData.User, TestData.Ref("missing"), Work, Ct));
        Assert.False(await this.Store.MoveAsync(Bob, TestData.Ref("a"), Work, Ct));

        Assert.Equal(Entry("a", minute: 4) with { Library = Work }, (await this.Store.ListAsync(TestData.User, null, Ct)).Single());
    }

    /// <summary>Search finds saved nodes by title across libraries, naming each one's library, and never other users' nodes.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SearchFindsTitlesAcrossLibraries()
    {
        await this.Store.CreateLibraryAsync(TestData.User, Work, this.Time.GetUtcNow(), Ct);
        await new SqliteNodeStore(this.Options, this.Time).UpsertAsync(
            [Titled("a", "One Piece"), Titled("b", "Piece of Cake"), Titled("c", "Bleach"), Titled("d", "100% piece_of")],
            Ct);
        await this.Store.AddAsync(Entry("a", minute: 1), Ct);
        await this.Store.AddAsync(Entry("b", minute: 2) with { Library = Work }, Ct);
        await this.Store.AddAsync(Entry("c", minute: 3), Ct);
        await this.Store.AddAsync(Entry("d", minute: 4), Ct);
        await this.Store.AddAsync(Entry("uncached", minute: 5), Ct);
        await this.Store.AddAsync(Entry("a", minute: 6) with { User = Bob }, Ct);

        IReadOnlyList<LibraryEntry> all = await this.Store.SearchAsync(TestData.User, "PIECE", null, 10, Ct);
        IReadOnlyList<LibraryEntry> inWork = await this.Store.SearchAsync(TestData.User, "piece", Work, 10, Ct);
        IReadOnlyList<LibraryEntry> limited = await this.Store.SearchAsync(TestData.User, "piece", null, 1, Ct);

        Assert.Equal(
            [("d", "Default"), ("b", "Work"), ("a", "Default")],
            all.Select(entry => (entry.Node.Id.Value, entry.Library.Value)));
        Assert.Equal([TestData.Ref("b")], inWork.Select(entry => entry.Node));
        Assert.Single(limited);
        Assert.Equal([TestData.Ref("d")], (await this.Store.SearchAsync(TestData.User, "100%", null, 10, Ct)).Select(entry => entry.Node));
        Assert.Equal([TestData.Ref("d")], (await this.Store.SearchAsync(TestData.User, "e_o", null, 10, Ct)).Select(entry => entry.Node));
        Assert.Empty(await this.Store.SearchAsync(TestData.User, "e?o", null, 10, Ct));
    }

    private static Node Titled(string id, string title) => new(TestData.Ref(id), NodeKind.Create("series"), title, null, null, []);

    private static LibraryEntry Entry(string node, int minute = 0) => new(
        TestData.User,
        LibraryName.Default,
        TestData.Ref(node),
        new DateTimeOffset(2026, 1, 1, 0, minute, 0, TimeSpan.Zero));
}
