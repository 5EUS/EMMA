using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Storage.Tests;

/// <summary>Tests for <see cref="SqliteAppSettings"/>.</summary>
public sealed class SqliteAppSettingsTests : StorageTestBase
{
    private static readonly UserId Bob = UserId.Create("bob");

    private SqliteAppSettings Store => new(this.Options);

    /// <summary>Values are stored by scope and key, replaced on a second write, and forgotten by a change with no value.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task StoresReplacesAndForgets()
    {
        await this.Store.ApplyAsync(TestData.User, "series:a", [new("reverse", "1"), new("groups", "[\"x\"]")], this.Time.GetUtcNow(), Ct);
        await this.Store.ApplyAsync(TestData.User, "series:a", [new("reverse", "0"), new("groups", null)], this.Time.GetUtcNow(), Ct);

        IReadOnlyDictionary<string, string> values = await this.Store.GetAsync(TestData.User, "series:a", Ct);

        Assert.Equal([KeyValuePair.Create("reverse", "0")], values);
    }

    /// <summary>Scopes and users do not see each other's values.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ScopesAndUsersAreSeparate()
    {
        await this.Store.ApplyAsync(TestData.User, "one", [new("k", "a")], this.Time.GetUtcNow(), Ct);
        await this.Store.ApplyAsync(TestData.User, "two", [new("k", "b")], this.Time.GetUtcNow(), Ct);
        await this.Store.ApplyAsync(Bob, "one", [new("k", "c")], this.Time.GetUtcNow(), Ct);

        Assert.Equal("a", (await this.Store.GetAsync(TestData.User, "one", Ct))["k"]);
        Assert.Equal("b", (await this.Store.GetAsync(TestData.User, "two", Ct))["k"]);
        Assert.Equal("c", (await this.Store.GetAsync(Bob, "one", Ct))["k"]);
        Assert.Empty(await this.Store.GetAsync(Bob, "two", Ct));
    }

    /// <summary>A scope that was never written is empty.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AnUnknownScopeIsEmpty() =>
        Assert.Empty(await this.Store.GetAsync(TestData.User, "nothing", Ct));
}
