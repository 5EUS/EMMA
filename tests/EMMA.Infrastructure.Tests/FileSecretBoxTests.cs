using System.Security.Cryptography;

using EMMA.Infrastructure.Secrets;

using Xunit;

namespace EMMA.Infrastructure.Tests;

/// <summary>Tests for <see cref="FileSecretBox"/>.</summary>
public sealed class FileSecretBoxTests : IDisposable
{
    private const string Context = "com.example.test\ntoken";

    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("emma-secretbox-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string KeyPath => Path.Combine(this.directory.FullName, "data", "secret.key");

    /// <inheritdoc/>
    public void Dispose() => this.directory.Delete(recursive: true);

    /// <summary>A secret opens again, including after a restart (a new box over the same key file), and the stored text does not contain it.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SecretRoundTripsAcrossBoxes()
    {
        using FileSecretBox first = new(this.KeyPath);
        string stored = await first.ProtectAsync("hunter2 é世", Context, Ct).ConfigureAwait(true);

        using FileSecretBox second = new(this.KeyPath);

        Assert.DoesNotContain("hunter2", stored, StringComparison.Ordinal);
        Assert.Equal("hunter2 é世", await second.UnprotectAsync(stored, Context, Ct).ConfigureAwait(true));
    }

    /// <summary>Protecting the same secret twice gives different text (a fresh nonce each time).</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SameSecretIsSealedDifferentlyEachTime()
    {
        using FileSecretBox box = new(this.KeyPath);

        string one = await box.ProtectAsync("abc", Context, Ct).ConfigureAwait(true);
        string two = await box.ProtectAsync("abc", Context, Ct).ConfigureAwait(true);

        Assert.NotEqual(one, two);
    }

    /// <summary>A stored value opens only for the context it was made for.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ValueDoesNotOpenForAnotherContext()
    {
        using FileSecretBox box = new(this.KeyPath);
        string stored = await box.ProtectAsync("abc", Context, Ct).ConfigureAwait(true);

        Assert.Null(await box.UnprotectAsync(stored, "com.example.other\ntoken", Ct).ConfigureAwait(true));
    }

    /// <summary>Damaged, truncated or foreign text reads as not openable instead of throwing.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DamagedTextIsNotOpenable()
    {
        using FileSecretBox box = new(this.KeyPath);
        string stored = await box.ProtectAsync("abc", Context, Ct).ConfigureAwait(true);
        byte[] bytes = Convert.FromBase64String(stored);
        bytes[^1] ^= 0xFF;

        Assert.Null(await box.UnprotectAsync(Convert.ToBase64String(bytes), Context, Ct).ConfigureAwait(true));
        Assert.Null(await box.UnprotectAsync("not base64 !!", Context, Ct).ConfigureAwait(true));
        Assert.Null(await box.UnprotectAsync(Convert.ToBase64String(bytes[..10]), Context, Ct).ConfigureAwait(true));
        Assert.Null(await box.UnprotectAsync(string.Empty, Context, Ct).ConfigureAwait(true));
    }

    /// <summary>With a new key file (the old one lost), old values cannot be opened, but the box works for new ones.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LosingTheKeyLosesTheValuesButNotTheBox()
    {
        using FileSecretBox before = new(this.KeyPath);
        string old = await before.ProtectAsync("abc", Context, Ct).ConfigureAwait(true);
        File.Delete(this.KeyPath);

        using FileSecretBox after = new(this.KeyPath);

        Assert.Null(await after.UnprotectAsync(old, Context, Ct).ConfigureAwait(true));
        Assert.Equal("new", await after.UnprotectAsync(await after.ProtectAsync("new", Context, Ct).ConfigureAwait(true), Context, Ct).ConfigureAwait(true));
    }

    /// <summary>The key file is created with a random 256-bit key, and on Unix is readable only by its owner.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task KeyFileIsCreatedPrivate()
    {
        using FileSecretBox box = new(this.KeyPath);

        await box.ProtectAsync("abc", Context, Ct).ConfigureAwait(true);

        Assert.Equal(32, new FileInfo(this.KeyPath).Length);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(this.KeyPath));
        }
    }

    /// <summary>Boxes racing to create the key (the CLI beside the API) all end up with the same one.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ConcurrentFirstUseSharesOneKey()
    {
        FileSecretBox[] boxes = [.. Enumerable.Range(0, 8).Select(_ => new FileSecretBox(this.KeyPath))];
        try
        {
            string[] stored = await Task.WhenAll(boxes.Select(box => box.ProtectAsync("abc", Context, Ct))).ConfigureAwait(true);

            foreach (string text in stored)
            {
                Assert.Equal("abc", await boxes[0].UnprotectAsync(text, Context, Ct).ConfigureAwait(true));
            }

            // Only the key itself is left: no half-written or temporary files.
            Assert.Equal([this.KeyPath], Directory.GetFileSystemEntries(Path.GetDirectoryName(this.KeyPath)!));
        }
        finally
        {
            foreach (FileSecretBox box in boxes)
            {
                box.Dispose();
            }
        }
    }

    /// <summary>A key file of the wrong size is reported, never silently replaced (that would lose every secret).</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DamagedKeyFileIsReported()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(this.KeyPath)!);
        await File.WriteAllBytesAsync(this.KeyPath, RandomNumberGenerator.GetBytes(10), Ct).ConfigureAwait(true);
        using FileSecretBox box = new(this.KeyPath);

        await Assert.ThrowsAsync<InvalidDataException>(() => box.ProtectAsync("abc", Context, Ct)).ConfigureAwait(true);
    }
}
