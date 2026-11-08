namespace EMMA.Domain;

/// <summary>Bytes read from a proxied asset.</summary>
/// <param name="Bytes">The bytes.</param>
/// <param name="Total">The total size when known.</param>
public sealed record Chunk(ReadOnlyMemory<byte> Bytes, long? Total);
