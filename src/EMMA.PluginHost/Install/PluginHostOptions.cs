using EMMA.Domain;

namespace EMMA.PluginHost.Install;

/// <summary>Where plugins are installed and the limits applied to packages. The host supplies these; nothing is read from the environment.</summary>
/// <param name="InstallRoot">The directory plugins are installed under, as <c>{InstallRoot}/{pluginId}/{version}/</c>.</param>
/// <param name="MaxPackageBytes">The largest package file accepted.</param>
/// <param name="MaxPackageEntries">The most entries a package may contain.</param>
/// <param name="MaxUnpackedBytes">The most bytes a package may expand to, counted as actually read, not as declared.</param>
public sealed record PluginHostOptions(
    string InstallRoot,
    long MaxPackageBytes = 256L * 1024 * 1024,
    int MaxPackageEntries = 64,
    long MaxUnpackedBytes = 512L * 1024 * 1024)
{
    /// <summary>Gets the directory a plugin version is installed into.</summary>
    /// <param name="id">The plugin id.</param>
    /// <param name="version">The plugin version.</param>
    /// <returns>The directory path.</returns>
    public string PluginDirectory(PluginId id, string version) => Path.Combine(this.InstallRoot, id.Value, version);
}
