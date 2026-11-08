using Xunit;

namespace EMMA.Storage.Tests;

/// <summary>Tests for <see cref="SqliteDelegationVersionStore"/>: rollback protection for delegation files.</summary>
public sealed class SqliteDelegationVersionStoreTests : StorageTestBase
{
    private SqliteDelegationVersionStore Store => new(this.Options, this.Time);

    /// <summary>A first, a repeated and a newer version are accepted; an older one is refused and changes nothing.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RefusesRollbacks()
    {
        Assert.True(await this.Store.TryAdvanceAsync("repo", 5, Ct));
        Assert.True(await this.Store.TryAdvanceAsync("repo", 5, Ct));
        Assert.True(await this.Store.TryAdvanceAsync("repo", 7, Ct));

        Assert.False(await this.Store.TryAdvanceAsync("repo", 6, Ct));
        Assert.Equal(7L, await this.ScalarAsync("SELECT version FROM trusted_delegation_versions WHERE repository_id = 'repo'"));
    }

    /// <summary>Each repository is tracked on its own.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RepositoriesAreIndependent()
    {
        Assert.True(await this.Store.TryAdvanceAsync("one", 9, Ct));

        Assert.True(await this.Store.TryAdvanceAsync("two", 1, Ct));
    }

    /// <summary>A negative version is never accepted.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task NegativeVersionsAreRefused() => Assert.False(await this.Store.TryAdvanceAsync("repo", -1, Ct));

    /// <summary>Many racing writers never let the stored version go down.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RacingWritersNeverLowerTheVersion()
    {
        await Task.WhenAll(Enumerable.Range(1, 40).Select(version => this.Store.TryAdvanceAsync("repo", version, Ct)));

        Assert.Equal(40L, await this.ScalarAsync("SELECT version FROM trusted_delegation_versions WHERE repository_id = 'repo'"));
    }
}
