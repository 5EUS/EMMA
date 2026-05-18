namespace EMMA.Domain;

/// <summary>
/// Named compression profiles used to derive media-specific artifacts.
/// </summary>
public enum CompressionProfile
{
    Original,
    Thumbnail,
    Preview,
    Streaming,
    Offline,
    Archival
}