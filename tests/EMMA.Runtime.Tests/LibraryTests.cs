using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Runtime.Tests;

/// <summary>Tests the library rules over the real stores: named libraries, saving, moving and searching.</summary>
public sealed class LibraryTests
{
    private static readonly UserId User = TestData.User;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Only nodes the host has served can be saved, to the default library unless one is named.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SavingNeedsASeenNode()
    {
        RuntimeHarness host = await StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            LibraryPipeline library = host.Runtime.Library;

            Outcome<bool> unseen = await library.AddAsync(User, TestData.Ref("never"), null, Ct).ConfigureAwait(true);
            Outcome<bool> added = await library.AddAsync(User, TestData.Ref("a"), null, Ct).ConfigureAwait(true);
            Outcome<bool> again = await library.AddAsync(User, TestData.Ref("a"), null, Ct).ConfigureAwait(true);

            Assert.Equal(ErrorCode.NotFound, unseen.Error?.Code);
            Assert.True(added.Value);
            Assert.False(again.Value);
            LibraryEntry entry = Assert.Single((await library.ListAsync(User, null, Ct).ConfigureAwait(true)).Value);
            Assert.Equal(LibraryName.Default, entry.Library);
        }
    }

    /// <summary>Libraries are created and deleted by name; the default one stays, bad names and duplicates are refused.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CreatingAndDeletingLibraries()
    {
        RuntimeHarness host = await StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            LibraryPipeline library = host.Runtime.Library;

            Outcome<LibrarySummary> created = await library.CreateAsync(User, "  Work  ", Ct).ConfigureAwait(true);
            Outcome<LibrarySummary> duplicate = await library.CreateAsync(User, "work", Ct).ConfigureAwait(true);
            Outcome<LibrarySummary> blank = await library.CreateAsync(User, "   ", Ct).ConfigureAwait(true);
            Outcome<LibrarySummary> tooLong = await library.CreateAsync(User, new string('x', LibraryName.MaxLength + 1), Ct).ConfigureAwait(true);
            Outcome<bool> deleteDefault = await library.DeleteAsync(User, "default", Ct).ConfigureAwait(true);
            Outcome<bool> deleteMissing = await library.DeleteAsync(User, "Nowhere", Ct).ConfigureAwait(true);

            Assert.Equal(new LibrarySummary(LibraryName.Create("Work"), host.Runtime.Time.GetUtcNow(), 0), created.Value);
            Assert.Equal(ErrorCode.InvalidArgument, duplicate.Error?.Code);
            Assert.Equal(ErrorCode.InvalidArgument, blank.Error?.Code);
            Assert.Equal(ErrorCode.InvalidArgument, tooLong.Error?.Code);
            Assert.Equal(ErrorCode.InvalidArgument, deleteDefault.Error?.Code);
            Assert.Equal(ErrorCode.NotFound, deleteMissing.Error?.Code);
            Assert.Equal(["Default", "Work"], (await library.LibrariesAsync(User, Ct).ConfigureAwait(true)).Select(item => item.Name.Value));

            Assert.True((await library.DeleteAsync(User, "WORK", Ct).ConfigureAwait(true)).IsSuccess);
            Assert.Equal(["Default"], (await library.LibrariesAsync(User, Ct).ConfigureAwait(true)).Select(item => item.Name.Value));
        }
    }

    /// <summary>A node is added to, listed in and moved between named libraries, and a library that does not exist is not found.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task NamedLibrariesHoldAndMoveNodes()
    {
        RuntimeHarness host = await StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            LibraryPipeline library = host.Runtime.Library;
            await library.CreateAsync(User, "Work", Ct).ConfigureAwait(true);

            Outcome<bool> toWork = await library.AddAsync(User, TestData.Ref("a"), "work", Ct).ConfigureAwait(true);
            Outcome<bool> toMissing = await library.AddAsync(User, TestData.Ref("b"), "Nowhere", Ct).ConfigureAwait(true);
            await library.AddAsync(User, TestData.Ref("b"), null, Ct).ConfigureAwait(true);

            Assert.True(toWork.Value);
            Assert.Equal(ErrorCode.NotFound, toMissing.Error?.Code);
            Assert.Equal(["a"], (await library.ListAsync(User, "Work", Ct).ConfigureAwait(true)).Value.Select(entry => entry.Node.Id.Value));
            Assert.Equal(["b"], (await library.ListAsync(User, "Default", Ct).ConfigureAwait(true)).Value.Select(entry => entry.Node.Id.Value));
            Assert.Equal(ErrorCode.NotFound, (await library.ListAsync(User, "Nowhere", Ct).ConfigureAwait(true)).Error?.Code);
            Assert.Equal(ErrorCode.InvalidArgument, (await library.ListAsync(User, string.Empty, Ct).ConfigureAwait(true)).Error?.Code);

            Assert.True((await library.MoveAsync(User, TestData.Ref("b"), "WORK", Ct).ConfigureAwait(true)).IsSuccess);
            Assert.Equal(ErrorCode.NotFound, (await library.MoveAsync(User, TestData.Ref("b"), "Nowhere", Ct).ConfigureAwait(true)).Error?.Code);
            Assert.Equal(ErrorCode.NotFound, (await library.MoveAsync(User, TestData.Ref("never"), "Work", Ct).ConfigureAwait(true)).Error?.Code);
            Assert.Equal(ErrorCode.InvalidArgument, (await library.MoveAsync(User, TestData.Ref("b"), null, Ct).ConfigureAwait(true)).Error?.Code);
            Assert.Equal(2, (await library.ListAsync(User, "Work", Ct).ConfigureAwait(true)).Value.Count);

            IReadOnlyList<LibrarySummary> counts = await library.LibrariesAsync(User, Ct).ConfigureAwait(true);
            Assert.Equal([0, 2], counts.Select(item => item.Count));
            Assert.True(await library.RemoveAsync(User, TestData.Ref("a"), Ct).ConfigureAwait(true));
            Assert.False(await library.RemoveAsync(User, TestData.Ref("a"), Ct).ConfigureAwait(true));
        }
    }

    /// <summary>Searching spans every library, names each result's library, can be limited to one, and refuses empty text.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SearchSpansLibraries()
    {
        RuntimeHarness host = await StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            LibraryPipeline library = host.Runtime.Library;
            await library.CreateAsync(User, "Work", Ct).ConfigureAwait(true);
            await library.AddAsync(User, TestData.Ref("a"), "Work", Ct).ConfigureAwait(true);
            await library.AddAsync(User, TestData.Ref("b"), null, Ct).ConfigureAwait(true);

            Outcome<IReadOnlyList<LibraryEntry>> all = await library.SearchAsync(User, " title ", null, 10, Ct).ConfigureAwait(true);
            Outcome<IReadOnlyList<LibraryEntry>> one = await library.SearchAsync(User, "title a", "work", 10, Ct).ConfigureAwait(true);
            Outcome<IReadOnlyList<LibraryEntry>> none = await library.SearchAsync(User, "title a", "Default", 10, Ct).ConfigureAwait(true);

            Assert.Equal([("a", "Work"), ("b", "Default")], all.Value.OrderBy(entry => entry.Node.Id.Value, StringComparer.Ordinal).Select(entry => (entry.Node.Id.Value, entry.Library.Value)));
            Assert.Equal(["a"], one.Value.Select(entry => entry.Node.Id.Value));
            Assert.Empty(none.Value);
            Assert.Equal(ErrorCode.InvalidArgument, (await library.SearchAsync(User, "  ", null, 10, Ct).ConfigureAwait(true)).Error?.Code);
            Assert.Equal(ErrorCode.NotFound, (await library.SearchAsync(User, "title", "Nowhere", 10, Ct).ConfigureAwait(true)).Error?.Code);
        }
    }

    private static async Task<RuntimeHarness> StartAsync()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(false);
        await host.Runtime.InitializeAsync(Ct).ConfigureAwait(false);
        NodeKind kind = NodeKind.Create("series");
        await host.Runtime.Nodes.UpsertAsync(
            [
                new Node(TestData.Ref("a"), kind, "Title A", null, null, []),
                new Node(TestData.Ref("b"), kind, "Title B", null, null, []),
            ],
            Ct).ConfigureAwait(false);
        return host;
    }
}
