using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Storage.Tests;

/// <summary>Tests for <see cref="SqliteNodeListingStore"/>.</summary>
public sealed class SqliteNodeListingStoreTests : StorageTestBase
{
    private SqliteNodeListingStore Store => new(this.Options, this.Time);

    /// <summary>A listing nobody stored is absent, not empty.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UnknownParentHasNoListing() =>
        Assert.Null(await this.Store.GetPageAsync(TestData.Ref("p"), 0, 10, Ct));

    /// <summary>Pages come back in the stored order with the whole listing's size.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task PagesKeepTheStoredOrder()
    {
        await this.Store.ReplaceAsync(TestData.Ref("p"), [Id("c3"), Id("c1"), Id("c2")], Ct);

        StoredListing? first = await this.Store.GetPageAsync(TestData.Ref("p"), 0, 2, Ct);
        StoredListing? rest = await this.Store.GetPageAsync(TestData.Ref("p"), 2, 2, Ct);
        StoredListing? beyond = await this.Store.GetPageAsync(TestData.Ref("p"), 9, 2, Ct);

        Assert.Equal(["c3", "c1"], first!.Children.Select(id => id.Value));
        Assert.Equal(["c2"], rest!.Children.Select(id => id.Value));
        Assert.Equal(3, first.Total);
        Assert.Empty(beyond!.Children);
    }

    /// <summary>Replacing a listing removes children that are no longer listed, and an empty listing is still a listing.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ReplacingDropsOldChildren()
    {
        await this.Store.ReplaceAsync(TestData.Ref("p"), [Id("a"), Id("b")], Ct);
        await this.Store.ReplaceAsync(TestData.Ref("p"), [Id("b")], Ct);
        await this.Store.ReplaceAsync(TestData.Ref("q"), [], Ct);

        Assert.Equal(["b"], (await this.Store.GetPageAsync(TestData.Ref("p"), 0, 10, Ct))!.Children.Select(id => id.Value));
        Assert.Equal(1L, await this.ScalarAsync("SELECT COUNT(*) FROM node_listings WHERE parent_local_id = 'p'"));
        Assert.Equal(0, (await this.Store.GetPageAsync(TestData.Ref("q"), 0, 10, Ct))!.Total);
    }

    /// <summary>Clearing forgets every listing.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ClearForgetsEveryListing()
    {
        await this.Store.ReplaceAsync(TestData.Ref("p"), [Id("a")], Ct);

        await this.Store.ClearAsync(Ct);

        Assert.Null(await this.Store.GetPageAsync(TestData.Ref("p"), 0, 10, Ct));
        Assert.Equal(0L, await this.ScalarAsync("SELECT COUNT(*) FROM node_listings"));
    }

    private static LocalNodeId Id(string value) => LocalNodeId.Create(value);
}
