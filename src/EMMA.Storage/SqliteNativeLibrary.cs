using System.Reflection;
using System.Runtime.InteropServices;

using SQLitePCL;

namespace EMMA.Storage;

/// <summary>
/// Controls how SQLite's native library is found and loaded.
/// <para>
/// Where: native libraries are normally found next to the running process; a library loaded into someone
/// else's process (an app embedding EMMA) is not, so its owner says where to look.
/// </para>
/// <para>
/// How: on Linux the library is loaded with <c>RTLD_DEEPBIND</c>. A host process often has another SQLite already
/// loaded (a Flutter app that plays video loads the system <c>libsqlite3</c> through libmpv and ffmpeg). Without
/// deep binding, the bundled library's own calls and globals are interposed by that earlier copy, SQLite's global
/// state is never initialised, and the first <c>sqlite3_open</c> jumps through a null pointer. Deep binding makes
/// the bundled copy resolve its own symbols first, and it is loaded locally so nothing else sees it.
/// </para>
/// </summary>
public static partial class SqliteNativeLibrary
{
    private const string LibraryName = "e_sqlite3";
    private const int RtldNow = 2;
    private const int RtldDeepBind = 8;

    private static readonly Lock Gate = new();
    private static readonly List<string> Directories = [];
    private static bool registered;

    /// <summary>Makes the runtime load SQLite itself, isolated from any other copy in the process. Safe to call repeatedly.</summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (!registered)
            {
                NativeLibrary.SetDllImportResolver(typeof(SQLite3Provider_e_sqlite3).Assembly, Resolve);
                registered = true;
            }
        }
    }

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
        }

        Register();
    }

    [LibraryImport("libc.so.6", EntryPoint = "dlopen", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial nint DlOpen(string path, int flags);

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(name, LibraryName, StringComparison.Ordinal))
        {
            return 0;
        }

        string[] directories;
        lock (Gate)
        {
            directories = [.. Directories, AppContext.BaseDirectory];
        }

        string file = FileName();
        foreach (string directory in directories)
        {
            nint handle = Load(Path.Combine(directory, file));
            if (handle != 0)
            {
                return handle;
            }
        }

        return NativeLibrary.TryLoad(name, assembly, searchPath, out nint fallback) ? fallback : 0;
    }

    private static nint Load(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                nint isolated = DlOpen(path, RtldNow | RtldDeepBind);
                if (isolated != 0)
                {
                    return isolated;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // No glibc dlopen (musl, or a very old glibc): load it the ordinary way below.
                _ = ex;
            }
        }

        return NativeLibrary.TryLoad(path, out nint handle) ? handle : 0;
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
