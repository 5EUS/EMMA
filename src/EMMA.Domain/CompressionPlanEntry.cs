namespace EMMA.Domain;

/// <summary>
/// Describes one derived artifact that a compression adapter should produce.
/// </summary>
public sealed record CompressionPlanEntry(
    CompressionProfile Profile,
    string ContentType,
    string? ContentEncoding = null,
    bool IsRequired = true,
    IReadOnlyDictionary<string, string>? Metadata = null);