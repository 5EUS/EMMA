namespace EMMA.PluginHost.Signing;

/// <summary>Supplies a repository's delegation file. How it is fetched (disk, bundled, remote) is the host's choice.</summary>
public interface IDelegationSource
{
    /// <summary>Gets a repository's delegation file.</summary>
    /// <param name="repositoryId">The repository id, already validated as a plain identifier.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The file's bytes, or <see langword="null"/> when there is none.</returns>
    Task<ReadOnlyMemory<byte>?> GetAsync(string repositoryId, CancellationToken cancellationToken);
}
