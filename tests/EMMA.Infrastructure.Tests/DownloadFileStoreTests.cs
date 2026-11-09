using EMMA.Infrastructure.Downloads;

using Xunit;

namespace EMMA.Infrastructure.Tests;

/// <summary>Tests for <see cref="DownloadFileStore"/>.</summary>
public sealed class DownloadFileStoreTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("emma-downloads-test-");

    /// <inheritdoc/>
    public void Dispose() => this.directory.Delete(recursive: true);

    /// <summary>Bytes written come back, and a rewrite replaces them.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task WritesAndReadsBack()
    {
        DownloadFileStore store = this.Store();

        await store.WriteAsync("abc123", new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2, 3], await store.ReadAsync("abc123", TestContext.Current.CancellationToken));

        await store.WriteAsync("abc123", new byte[] { 9 }, TestContext.Current.CancellationToken);
        Assert.Equal([9], await store.ReadAsync("abc123", TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(this.directory.FullName));
    }

    /// <summary>A file that was never written, or whose directory does not exist, reads as nothing.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AMissingFileReadsAsNull()
    {
        Assert.Null(await this.Store().ReadAsync("abc123", TestContext.Current.CancellationToken));
        Assert.Null(await new DownloadFileStore(Path.Combine(this.directory.FullName, "absent")).ReadAsync("abc123", TestContext.Current.CancellationToken));
    }

    /// <summary>Deleting removes the named files and ignores names that are not there.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DeletesNamedFiles()
    {
        DownloadFileStore store = this.Store();
        await store.WriteAsync("aa", new byte[] { 1 }, TestContext.Current.CancellationToken);
        await store.WriteAsync("bb", new byte[] { 2 }, TestContext.Current.CancellationToken);

        await store.DeleteAsync(["aa", "cc"], TestContext.Current.CancellationToken);

        Assert.Null(await store.ReadAsync("aa", TestContext.Current.CancellationToken));
        Assert.NotNull(await store.ReadAsync("bb", TestContext.Current.CancellationToken));
    }

    /// <summary>A name that could leave the directory is refused by every operation.</summary>
    /// <param name="name">The name.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("ABC")]
    [InlineData("")]
    public async Task RefusesNamesThatAreNotHex(string name)
    {
        DownloadFileStore store = this.Store();

        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(name, new byte[] { 1 }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync(name, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync([name], TestContext.Current.CancellationToken));
    }

    /// <summary>Starting up removes the half-written files a crash left, and nothing else.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task InitializeRemovesPartialFiles()
    {
        DownloadFileStore store = this.Store();
        await store.WriteAsync("aa", new byte[] { 1 }, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(this.directory.FullName, "bb.part"), [2], TestContext.Current.CancellationToken);

        await store.InitializeAsync();

        Assert.Equal(["aa"], Directory.GetFiles(this.directory.FullName).Select(Path.GetFileName));
    }

    private DownloadFileStore Store() => new(this.directory.FullName);
}
