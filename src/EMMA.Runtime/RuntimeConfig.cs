using EMMA.Domain;
using EMMA.PluginHost.Signing;

namespace EMMA.Runtime;

/// <summary>The settings a non-server host (the native library, the CLI) reads from a JSON document.</summary>
/// <param name="DataDirectory">The directory EMMA keeps its state in.</param>
/// <param name="User">The user whose progress and library this host reads and writes.</param>
/// <param name="Trust">Which plugin signatures are accepted.</param>
/// <param name="NativeLibraryDirectory">The directory holding the native libraries, when they are not next to the executable.</param>
public sealed record RuntimeConfig(string DataDirectory, UserId User, TrustPolicy Trust, string? NativeLibraryDirectory = null)
{
    /// <summary>Gets the user used when the document names none.</summary>
    public static UserId DefaultUser { get; } = UserId.Create("local");

    /// <summary>Converts the settings to runtime options.</summary>
    /// <returns>The options.</returns>
    public EmmaRuntimeOptions ToOptions() => new(this.DataDirectory) { Trust = this.Trust, NativeLibraryDirectory = this.NativeLibraryDirectory };
}
