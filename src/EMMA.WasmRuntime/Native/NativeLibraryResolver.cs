using System.Reflection;
using System.Runtime.InteropServices;

namespace EMMA.WasmRuntime.Native;

/// <summary>
/// Finds libemma_wasm for the <c>[LibraryImport]</c> stubs. It looks next to the application
/// (including <c>runtimes/&lt;rid&gt;/native</c>) and then in the system's default locations; the
/// <c>EMMA_WASM_LIBRARY</c> environment variable names an explicit file for development and tests.
/// </summary>
internal static class NativeLibraryResolver
{
    /// <summary>The environment variable that names the library file.</summary>
    internal const string OverrideVariable = "EMMA_WASM_LIBRARY";

    private static readonly Lock Gate = new();
    private static readonly List<string> Directories = [];
    private static bool registered;

    /// <summary>Adds a directory searched for the native library before the default places.</summary>
    /// <param name="directory">The directory.</param>
    internal static void AddDirectory(string directory)
    {
        lock (Gate)
        {
            if (!Directories.Contains(directory, StringComparer.Ordinal))
            {
                Directories.Add(directory);
            }
        }
    }

    /// <summary>Installs the resolver once for this assembly.</summary>
    internal static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (!registered)
            {
                NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
                registered = true;
            }
        }
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(name, NativeMethods.Library, StringComparison.Ordinal))
        {
            return 0;
        }

        string? explicitPath = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrEmpty(explicitPath) && NativeLibrary.TryLoad(explicitPath, out nint explicitHandle))
        {
            return explicitHandle;
        }

        foreach (string candidate in Candidates())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out nint handle))
            {
                return handle;
            }
        }

        return NativeLibrary.TryLoad(name, assembly, searchPath, out nint fallback) ? fallback : 0;
    }

    private static IEnumerable<string> Candidates()
    {
        string file = LibraryFileName();
        string[] configured;
        lock (Gate)
        {
            configured = [.. Directories];
        }

        foreach (string directory in configured)
        {
            yield return Path.Combine(directory, file);
        }

        string baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, file);
        yield return Path.Combine(baseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", file);
    }

    private static string LibraryFileName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "emma_wasm.dll";
        }

        return OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? "libemma_wasm.dylib" : "libemma_wasm.so";
    }
}
