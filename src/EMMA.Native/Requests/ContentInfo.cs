namespace EMMA.Native.Requests;

/// <summary>Describes the bytes returned beside the JSON of an asset read.</summary>
/// <param name="Mime">The type the plugin declared.</param>
/// <param name="Length">The number of bytes in the blob.</param>
/// <param name="RangeStart">For a partial read, the offset of the first byte.</param>
/// <param name="Total">The size of the whole asset, when known.</param>
/// <param name="AcceptsRanges">Whether the asset can be read in parts.</param>
internal sealed record ContentInfo(
    string Mime,
    long Length,
    long? RangeStart,
    long? Total,
    bool AcceptsRanges);
