namespace EMMA.Api.Configuration;

/// <summary>Everything the host needs to run, read once and validated at startup.</summary>
/// <param name="DataDirectory">The directory EMMA keeps its state in.</param>
/// <param name="Trust">Which plugin signatures are accepted.</param>
/// <param name="Security">Authentication and rate limits.</param>
internal sealed record ApiSettings(string DataDirectory, TrustSettings Trust, SecuritySettings Security)
{
    /// <summary>Gets the SQLite database file.</summary>
    public string DatabasePath => Path.Combine(this.DataDirectory, "emma.db");

    /// <summary>Gets the directory installed plugins live in.</summary>
    public string InstallRoot => Path.Combine(this.DataDirectory, "plugins");

    /// <summary>Gets the directory holding each repository's signed delegation file.</summary>
    public string DelegationsDirectory => Path.Combine(this.DataDirectory, "delegations");

    /// <summary>Gets the directory the fetched-asset cache spills to.</summary>
    public string AssetCacheRoot => Path.Combine(this.DataDirectory, "cache", "assets");
}
