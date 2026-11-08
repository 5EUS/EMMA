using EMMA.PluginHost.Manifest;

namespace EMMA.PluginHost.Signing;

/// <summary>
/// Reads <c>{repositoryId}.delegations.json</c> from one directory. The repository id is
/// validated as a plain identifier first, so it can never name a file outside the directory
/// (the repository id comes from the package source, but a plugin's own signature block
/// repeats it, and only the source's value is ever used here).
/// </summary>
/// <param name="directory">The directory holding the delegation files.</param>
public sealed class FileDelegationSource(string directory) : IDelegationSource
{
    /// <inheritdoc/>
    public async Task<ReadOnlyMemory<byte>?> GetAsync(string repositoryId, CancellationToken cancellationToken)
    {
        if (!ManifestRules.IsIdentifier(repositoryId))
        {
            return null;
        }

        string path = Path.Combine(directory, $"{repositoryId}.delegations.json");
        FileInfo file = new(path);
        if (!file.Exists || file.Length > DelegationJson.MaxBytes)
        {
            return null;
        }

        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
