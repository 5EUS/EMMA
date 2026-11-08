using System.IO.Compression;

using EMMA.PluginHost.Manifest;

namespace EMMA.PluginHost.Install;

/// <summary>
/// Checks every entry of an untrusted package before anything is read or written: entry names must
/// be plain relative paths (compared as whole segments, never by string prefix), unique, not
/// symbolic links, and within the size and count limits.
/// </summary>
internal static class PackageValidator
{
    /// <summary>The name of the manifest entry every package must have, at its root.</summary>
    public const string ManifestEntryName = "plugin.json";

    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymlink = 0xA000;

    /// <summary>Checks the structure of a package.</summary>
    /// <param name="archive">The opened package.</param>
    /// <param name="options">The limits.</param>
    /// <returns>A description of the first problem, or <see langword="null"/> when the package is structurally sound.</returns>
    public static string? FindProblem(ZipArchive archive, PluginHostOptions options)
    {
        if (archive.Entries.Count > options.MaxPackageEntries)
        {
            return $"the package has more than {options.MaxPackageEntries} entries";
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        long declaredTotal = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            bool isDirectory = entry.FullName.EndsWith('/') && entry.Length == 0;
            string name = isDirectory ? entry.FullName.TrimEnd('/') : entry.FullName;

            if (!ManifestRules.IsSafeRelativePath(name))
            {
                return $"the package has an entry with an unsafe name ('{Printable(entry.FullName)}')";
            }

            if (!seen.Add(name))
            {
                return $"the package has two entries named '{name}' (names are compared ignoring case)";
            }

            if (IsSymbolicLink(entry))
            {
                return $"the package has a symbolic link ('{name}')";
            }

            declaredTotal += entry.Length;
            if (entry.Length > options.MaxUnpackedBytes || declaredTotal > options.MaxUnpackedBytes)
            {
                return "the package expands to more than the allowed size";
            }
        }

        return null;
    }

    /// <summary>Finds the files a package contains, ignoring directory entries.</summary>
    /// <param name="archive">The opened package.</param>
    /// <returns>The file entries.</returns>
    public static IReadOnlyList<ZipArchiveEntry> Files(ZipArchive archive) =>
        [.. archive.Entries.Where(entry => !(entry.FullName.EndsWith('/') && entry.Length == 0))];

    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & UnixFileTypeMask) == UnixSymlink;

    private static string Printable(string name) =>
        new([.. name.Take(80).Select(character => char.IsControl(character) ? '?' : character)]);
}
