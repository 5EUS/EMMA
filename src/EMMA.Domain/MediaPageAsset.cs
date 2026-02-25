namespace EMMA.Domain;

/// <summary>
/// Raw page asset payload with basic metadata.
/// </summary>
public sealed record MediaPageAsset(
    string ContentType,
    byte[] Payload,
    DateTimeOffset FetchedAtUtc);
