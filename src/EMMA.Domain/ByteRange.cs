namespace EMMA.Domain;

/// <summary>A byte range to read from a proxied asset.</summary>
/// <param name="Start">The first byte.</param>
/// <param name="Length">The number of bytes; <see langword="null"/> means to the end.</param>
public sealed record ByteRange(long Start, long? Length);
