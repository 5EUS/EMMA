using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// Plans and generates compressed artifacts for one or more media types.
/// </summary>
public interface ICompressionAdapterPort
{
    /// <summary>
    /// Media types this adapter can handle.
    /// </summary>
    IReadOnlyList<MediaType> SupportedMediaTypes { get; }

    /// <summary>
    /// Builds a compression plan for the requested source and profile.
    /// </summary>
    Task<CompressionPlan> PlanAsync(CompressionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Produces one or more compressed artifacts for the requested source.
    /// </summary>
    Task<IReadOnlyList<CompressionArtifact>> GenerateAsync(CompressionRequest request, CancellationToken cancellationToken);
}