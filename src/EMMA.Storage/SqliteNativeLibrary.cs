using System.Reflection;
using System.Runtime.InteropServices;

using SQLitePCL;

namespace EMMA.Storage;

/// <summary>
/// Tells SQLite where its native library is. Native libraries are normally found next to the running process; a
/// library loaded into someone else's process (an app embedding EMMA) is not, so its owner says where to look.
/// </summary>
public static class SqliteNativeLibrary
{
    private const string LibraryName = "e_sqlite3";

    private static readonly Lock Gate = new();
    private static readonly List<string> Directories = [];
    private static bool registered;

    /// <summary>Adds a directory to search for the SQLite native library, ahead of the default places.</summary>
    /// <param name="directory">The directory holding <c>libe_sqlite3</c>.</param>
    public static void AddSearchDirectory(string directory)
    {
        lock (Gate)
        {
            if (!Directories.Contains(directory, StringComparer.Ordinal))
            {
                Directories.Add(directory);
            }

            if (!registered)
            {
                NativeLibrary.SetDllImportResolver(typeof(SQLite3Provider_e_sqlite3).Assembly, Resolve);
                registered = true;
            }
        }
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(name, LibraryName, StringComparison.Ordinal))
        {
            return 0;
        }

        string[] directories;
        lock (Gate)
        {
            directories = [.. Directories];
        }

        string file = FileName();
        foreach (string directory in directories)
        {
            if (NativeLibrary.TryLoad(Path.Combine(directory, file), out nint handle))
            {
                return handle;
            }
        }

        return NativeLibrary.TryLoad(name, assembly, searchPath, out nint fallback) ? fallback : 0;
    }

    private static string FileName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "e_sqlite3.dll";
        }

        return OperatingSystem.IsMacOS() ? "libe_sqlite3.dylib" : "libe_sqlite3.so";
    }
}
