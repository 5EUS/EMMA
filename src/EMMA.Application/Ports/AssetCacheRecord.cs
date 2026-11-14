using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>One cached asset file, as remembered by an <see cref="IAssetCacheIndexPort"/>.</summary>
/// <param name="Key">The cache key text (<see cref="AssetKey.ToString"/>).</param>
/// <param name="Node">The node the asset belongs to.</param>
/// <param name="File">The file name inside the cache directory (never a full path, so the directory can move).</param>
/// <param name="Mime">The payload type.</param>
/// <param name="Bytes">The file size.</param>
/// <param name="FetchedAt">When the bytes were fetched.</param>
/// <param name="LastUsedAt">When the entry was last read or written.</param>
public sealed record AssetCacheRecord(
    string Key,
    NodeRef Node,
    string File,
    string Mime,
    long Bytes,
    DateTimeOffset FetchedAt,
    DateTimeOffset LastUsedAt);
