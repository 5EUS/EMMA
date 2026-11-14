namespace EMMA.Application.Ports;

/// <summary>
/// An open response from a remote asset, for bytes too large to hold at once. The body is read as it arrives; disposing
/// the value releases the connection.
/// </summary>
public sealed class RemoteResponse : IAsyncDisposable
{
    private readonly IDisposable? owner;

    /// <summary>Initializes a new instance of the <see cref="RemoteResponse"/> class.</summary>
    /// <param name="mime">The type the upstream sent.</param>
    /// <param name="length">The number of bytes in <paramref name="body"/>, when known.</param>
    /// <param name="rangeStart">For a partial response, the offset of its first byte; otherwise <see langword="null"/>.</param>
    /// <param name="total">The size of the whole resource, when known.</param>
    /// <param name="acceptsRanges">Whether the upstream can serve byte ranges.</param>
    /// <param name="body">The bytes.</param>
    /// <param name="owner">Released with the stream, such as the HTTP response the body belongs to.</param>
    public RemoteResponse(string mime, long? length, long? rangeStart, long? total, bool acceptsRanges, Stream body, IDisposable? owner = null)
    {
        this.Mime = mime;
        this.Length = length;
        this.RangeStart = rangeStart;
        this.Total = total;
        this.AcceptsRanges = acceptsRanges;
        this.Body = body;
        this.owner = owner;
    }

    /// <summary>Gets the type the upstream sent.</summary>
    public string Mime { get; }

    /// <summary>Gets the number of bytes in <see cref="Body"/>, when known.</summary>
    public long? Length { get; }

    /// <summary>Gets the offset of the first byte of a partial response; <see langword="null"/> when the body is the whole resource.</summary>
    public long? RangeStart { get; }

    /// <summary>Gets the size of the whole resource, when known.</summary>
    public long? Total { get; }

    /// <summary>Gets a value indicating whether the upstream can serve byte ranges.</summary>
    public bool AcceptsRanges { get; }

    /// <summary>Gets the bytes.</summary>
    public Stream Body { get; }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // The response was handed over together with its body on purpose: it is the owner and releases both.
#pragma warning disable IDISP007
        await this.Body.DisposeAsync().ConfigureAwait(false);
        this.owner?.Dispose();
#pragma warning restore IDISP007
    }
}
