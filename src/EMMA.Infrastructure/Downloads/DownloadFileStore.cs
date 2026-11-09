using EMMA.Application.Ports;

namespace EMMA.Infrastructure.Downloads;

/// <summary>
/// Keeps downloaded assets as files in one directory. A file is written next to its final name and moved into
/// place, so a reader sees all of a file or none of it, and a crash leaves at most a stray <c>.part</c> file that
/// the next start removes. Names are checked, so a name can never leave the directory.
/// </summary>
/// <param name="root">The directory. It is created when first needed.</param>
public sealed class DownloadFileStore(string root) : IDownloadFilePort
{
    private const int MaxNameLength = 64;
    private const string PartSuffix = ".part";

    /// <summary>Removes the half-written files a crash left behind. Call once at startup.</summary>
    /// <returns>A task that completes when they are gone.</returns>
    public Task InitializeAsync()
    {
        if (Directory.Exists(root))
        {
            foreach (string path in Directory.EnumerateFiles(root, "*" + PartSuffix))
            {
                File.Delete(path);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        string path = this.PathOf(name);
        Directory.CreateDirectory(root);
        string partial = path + PartSuffix;
        await File.WriteAllBytesAsync(partial, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(partial, path, overwrite: true);
    }

    /// <inheritdoc/>
    public async Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        string path = this.PathOf(name);
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public Task DeleteAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        foreach (string name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // File.Delete does nothing for a file that does not exist.
            File.Delete(this.PathOf(name));
        }

        return Task.CompletedTask;
    }

    private static bool IsValidName(string name) =>
        name.Length is > 0 and <= MaxNameLength && name.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private string PathOf(string name) => IsValidName(name)
        ? Path.Combine(root, name)
        : throw new ArgumentException("A download file name is 1 to 64 lower-case hexadecimal digits.", nameof(name));
}
