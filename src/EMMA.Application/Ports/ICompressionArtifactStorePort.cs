using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// Stores and retrieves derived compression artifacts separately from canonical media assets.
/// </summary>
public interface ICompressionArtifactStorePort
{
    /// <summary>
    /// Retrieves a stored artifact by its stable artifact identifier.
    /// </summary>
    Task<CompressionArtifact?> GetAsync(string artifactId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists stored artifacts for a media item.
    /// </summary>
    Task<IReadOnlyList<CompressionArtifact>> ListAsync(MediaId mediaId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores or updates a derived artifact.
    /// </summary>
    Task UpsertAsync(CompressionArtifact artifact, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a derived artifact.
    /// </summary>
    Task DeleteAsync(string artifactId, CancellationToken cancellationToken);
}