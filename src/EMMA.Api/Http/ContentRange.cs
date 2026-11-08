namespace EMMA.Api.Http;

/// <summary>Where a partial response sits within the whole asset.</summary>
/// <param name="Start">The first byte sent.</param>
/// <param name="Total">The size of the whole asset, when known.</param>
internal sealed record ContentRange(long Start, long? Total);
