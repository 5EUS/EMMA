namespace EMMA.Domain;

/// <summary>
/// Describes a request to derive a compressed artifact from a canonical source.
/// </summary>
public sealed record CompressionRequest(
    MediaId MediaId,
    MediaType MediaType,
    string SourceKey,
    CompressionProfile Profile,
    string? SourceRevision = null,
    string? PreferredContentType = null,
    bool PreferLossless = false,
    IReadOnlyDictionary<string, string>? Hints = null);