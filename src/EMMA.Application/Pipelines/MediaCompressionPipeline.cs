using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// Orchestrates compression planning, artifact generation, and artifact persistence.
/// </summary>
public sealed class MediaCompressionPipeline(
    ICompressionRegistryPort registry,
    ICompressionArtifactStorePort artifactStore)
{
    private readonly ICompressionRegistryPort _registry = registry;
    private readonly ICompressionArtifactStorePort _artifactStore = artifactStore;

    /// <summary>
    /// Builds a compression plan for the supplied request.
    /// </summary>
    public Task<CompressionPlan> PlanAsync(CompressionRequest request, CancellationToken cancellationToken)
        => ResolveAdapter(request.MediaType).PlanAsync(request, cancellationToken);

    /// <summary>
    /// Returns a previously generated artifact when available, otherwise generates and stores it.
    /// </summary>
    public async Task<CompressionArtifact> GetOrCreateAsync(
        CompressionRequest request,
        CancellationToken cancellationToken)
    {
        var cached = await TryGetExistingAsync(request, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var generated = await GenerateAsync(request, cancellationToken);
        var match = generated.FirstOrDefault(artifact => ArtifactMatches(artifact, request));
        if (match is not null)
        {
            return match;
        }

        throw new InvalidOperationException(
            $"Compression adapter for media type '{request.MediaType}' did not produce a matching artifact for profile '{request.Profile}'.");
    }

    /// <summary>
    /// Generates artifacts for the supplied request and persists them in the configured store.
    /// </summary>
    public async Task<IReadOnlyList<CompressionArtifact>> GenerateAsync(
        CompressionRequest request,
        CancellationToken cancellationToken)
    {
        var adapter = ResolveAdapter(request.MediaType);
        var plan = await adapter.PlanAsync(request, cancellationToken);

        if (plan.Outputs.Count == 0)
        {
            throw new InvalidOperationException(
                $"Compression plan for media type '{request.MediaType}' produced no outputs.");
        }

        var artifacts = await adapter.GenerateAsync(request, cancellationToken);
        if (artifacts.Count == 0)
        {
            throw new InvalidOperationException(
                $"Compression adapter for media type '{request.MediaType}' generated no artifacts.");
        }

        foreach (var artifact in artifacts)
        {
            await _artifactStore.UpsertAsync(artifact, cancellationToken);
        }

        return artifacts;
    }

    private async Task<CompressionArtifact?> TryGetExistingAsync(
        CompressionRequest request,
        CancellationToken cancellationToken)
    {
        var artifacts = await _artifactStore.ListAsync(request.MediaId, cancellationToken);
        return artifacts.FirstOrDefault(artifact => ArtifactMatches(artifact, request));
    }

    private ICompressionAdapterPort ResolveAdapter(MediaType mediaType)
        => _registry.Resolve(mediaType)
           ?? throw new InvalidOperationException($"No compression adapter is registered for media type '{mediaType}'.");

    private static bool ArtifactMatches(CompressionArtifact artifact, CompressionRequest request)
        => artifact.MediaId == request.MediaId
           && artifact.MediaType == request.MediaType
           && artifact.SourceKey == request.SourceKey
           && artifact.Profile == request.Profile
           && string.Equals(artifact.SourceRevision, request.SourceRevision, StringComparison.Ordinal);
}