using System.Collections.Concurrent;
using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.InMemory;

/// <summary>
/// In-memory storage for derived compression artifacts.
/// </summary>
public sealed class InMemoryCompressionArtifactStore : ICompressionArtifactStorePort
{
    private readonly ConcurrentDictionary<string, CompressionArtifact> _artifacts = new(StringComparer.Ordinal);

    public Task<CompressionArtifact?> GetAsync(string artifactId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _artifacts.TryGetValue(artifactId, out var artifact);
        return Task.FromResult<CompressionArtifact?>(artifact);
    }

    public Task<IReadOnlyList<CompressionArtifact>> ListAsync(MediaId mediaId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<CompressionArtifact> artifacts = _artifacts.Values
            .Where(artifact => artifact.MediaId == mediaId)
            .OrderBy(artifact => artifact.CreatedAtUtc)
            .ToList();

        return Task.FromResult(artifacts);
    }

    public Task UpsertAsync(CompressionArtifact artifact, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _artifacts[artifact.ArtifactId] = artifact;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string artifactId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _artifacts.TryRemove(artifactId, out _);
        return Task.CompletedTask;
    }
}