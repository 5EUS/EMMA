namespace EMMA.Domain;

/// <summary>Fetched asset bytes with their type and when they were fetched.</summary>
/// <param name="Mime">The payload type.</param>
/// <param name="Payload">The bytes.</param>
/// <param name="FetchedAt">When the bytes were fetched.</param>
public sealed record AssetPayload(string Mime, ReadOnlyMemory<byte> Payload, DateTimeOffset FetchedAt);
