namespace EMMA.Domain;

/// <summary>
/// Stores one derived compressed artifact and its identifying metadata.
/// </summary>
public sealed record CompressionArtifact(
    string ArtifactId,
    MediaId MediaId,
    MediaType MediaType,
    string SourceKey,
    CompressionProfile Profile,
    string ContentType,
    byte[] Payload,
    DateTimeOffset CreatedAtUtc,
    string? ContentEncoding = null,
    string? SourceRevision = null,
    IReadOnlyDictionary<string, string>? Metadata = null);