using System.Runtime.InteropServices;

namespace EMMA.Runtime;

/// <summary>
/// Points .NET's TLS stack at Android's certificate authorities. Everywhere else the platform supplies them: a Linux
/// distribution has <c>/etc/ssl/certs</c>, and Windows, macOS and iOS have their own trust stores. .NET's NativeAOT
/// <c>linux-bionic</c> runtime builds certificate chains through OpenSSL, which looks in <c>SSL_CERT_DIR</c> and then in a
/// compiled-in directory that does not exist on a phone, so every HTTPS request fails to validate the server.
/// </summary>
internal static partial class SystemCertificates
{
    /// <summary>The directory OpenSSL and .NET read trusted roots from.</summary>
    internal const string DirectoryVariable = "SSL_CERT_DIR";

    /// <summary>The single bundle file OpenSSL and .NET read trusted roots from.</summary>
    internal const string FileVariable = "SSL_CERT_FILE";

    // The conscrypt APEX is the updatable store (Android 14 and later); /system/etc/security/cacerts is the same list on
    // older releases. Both hold one PEM file per root and any app can read them.
    private static readonly string[] AndroidDirectories =
    [
        "/apex/com.android.conscrypt/cacerts",
        "/system/etc/security/cacerts",
    ];

    /// <summary>
    /// Sets <c>SSL_CERT_DIR</c> to Android's store when this process is on Android and nothing else has chosen a source of
    /// roots. It does nothing on any other platform, which has no such directory.
    /// </summary>
    internal static void Configure() => Configure(Environment.GetEnvironmentVariable, Directory.Exists, SetNativeEnvironment);

    /// <summary>Does the work of <see cref="Configure()"/> against the given environment and file system.</summary>
    /// <param name="get">Reads an environment variable, or returns null.</param>
    /// <param name="directoryExists">Whether a directory exists.</param>
    /// <param name="set">Sets an environment variable that native code can see.</param>
    /// <returns>The directory chosen, or null when nothing was changed.</returns>
    internal static string? Configure(Func<string, string?> get, Func<string, bool> directoryExists, Action<string, string> set)
    {
        // A host or a user who set either variable chose where the roots come from; respect that.
        if (!string.IsNullOrEmpty(get(DirectoryVariable)) || !string.IsNullOrEmpty(get(FileVariable)))
        {
            return null;
        }

        string? directory = Array.Find(AndroidDirectories, directoryExists.Invoke);
        if (directory is not null)
        {
            set(DirectoryVariable, directory);
        }

        return directory;
    }

    private static void SetNativeEnvironment(string name, string value)
    {
        // Environment.SetEnvironmentVariable only changes a copy that managed code keeps on Unix; OpenSSL calls getenv.
        if (SetEnv(name, value, 1) != 0)
        {
            throw new InvalidOperationException($"Could not set {name} for the process.");
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetEnv(string name, string value, int overwrite);
}
