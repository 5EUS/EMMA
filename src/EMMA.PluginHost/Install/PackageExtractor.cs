using System.IO.Compression;
using System.Security.Cryptography;

using EMMA.Domain;

namespace EMMA.PluginHost.Install;

/// <summary>Copies package entries to disk, counting the bytes actually read rather than trusting declared sizes.</summary>
internal static class PackageExtractor
{
    private const int BufferSize = 81920;

    /// <summary>Reads a small entry fully into memory.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="maxBytes">The most bytes accepted.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes, or <see langword="null"/> when the entry is larger than allowed.</returns>
    public static async Task<byte[]?> ReadSmallAsync(ZipArchiveEntry entry, int maxBytes, CancellationToken cancellationToken)
    {
        Stream source = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            using MemoryStream buffer = new();
            byte[] chunk = new byte[BufferSize];
            int read;
            while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return null;
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return buffer.ToArray();
        }
    }

    /// <summary>
    /// Extracts one entry beneath a staging directory while hashing it. The destination is resolved
    /// and then checked against the staging directory by relative path, not by string prefix.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="stagingRoot">The directory to extract into.</param>
    /// <param name="relativePath">The validated relative path, with forward slashes.</param>
    /// <param name="maxBytes">The most bytes accepted.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>The SHA-256 of what was written as lower-case hex, or a failure.</returns>
    public static async Task<Outcome<string>> ExtractAsync(
        ZipArchiveEntry entry,
        string stagingRoot,
        string relativePath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        string destination = Path.GetFullPath(Path.Combine(stagingRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string relative = Path.GetRelativePath(stagingRoot, destination);
        if (Path.IsPathRooted(relative) || string.Equals(relative, "..", StringComparison.Ordinal) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return Outcome.Failure<string>(ErrorCode.InvalidArgument, $"The package entry '{relativePath}' would be written outside the install directory.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Stream source = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            FileStream target = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            await using (target.ConfigureAwait(false))
            {
                byte[] chunk = new byte[BufferSize];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        return Outcome.Failure<string>(ErrorCode.InvalidArgument, $"The package entry '{relativePath}' is larger than allowed.");
                    }

                    hash.AppendData(chunk, 0, read);
                    await target.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return Outcome.Success(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}
