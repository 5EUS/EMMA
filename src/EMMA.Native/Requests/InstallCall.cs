namespace EMMA.Native.Requests;

/// <summary>Installs a plugin package from a file.</summary>
/// <param name="PackagePath">The path of the zip.</param>
/// <param name="Repository">The repository id the package came from, as known to the host.</param>
internal sealed record InstallCall(
    string? PackagePath,
    string? Repository);
