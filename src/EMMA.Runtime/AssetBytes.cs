namespace EMMA.Runtime;

/// <summary>The bytes of one asset, or of a range of it.</summary>
/// <param name="Mime">The type the plugin declared.</param>
/// <param name="Bytes">The content. Empty for a range that starts past the end.</param>
/// <param name="RangeStart">For a partial read, the offset of the first byte; otherwise <see langword="null"/>.</param>
/// <param name="Total">The size of the whole asset, when known and the read was partial.</param>
/// <param name="AcceptsRanges">Whether the asset can be read in parts.</param>
public sealed record AssetBytes(string Mime, ReadOnlyMemory<byte> Bytes, long? RangeStart, long? Total, bool AcceptsRanges)
{
    /// <summary>Gets a value indicating whether a range was asked for but starts beyond the end of the asset.</summary>
    public bool RangeNotSatisfiable => this.RangeStart is not null && this.Bytes.IsEmpty;
}
