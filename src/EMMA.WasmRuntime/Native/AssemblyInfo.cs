using System.Runtime.InteropServices;

// NativeLibraryResolver loads the library by absolute path from the application directory. Anything it
// does not find is looked up only in the system directories, never the current directory (CA5392, CA5393).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
