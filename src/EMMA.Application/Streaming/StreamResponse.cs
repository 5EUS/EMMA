namespace EMMA.Application.Streaming;

/// <summary>The answer to a <see cref="StreamRequest"/>. Disposing it releases the body.</summary>
public sealed class StreamResponse : IAsyncDisposable
{
    private readonly IAsyncDisposable? owner;

    /// <summary>Initializes a new instance of the <see cref="StreamResponse"/> class.</summary>
    /// <param name="mime">The type of the body.</param>
    /// <param name="body">The bytes; the response owns the stream.</param>
    /// <param name="length">The number of bytes in the body, when known.</param>
    /// <param name="rangeStart">For a partial answer, the offset of the first byte; otherwise <see langword="null"/>.</param>
    /// <param name="total">The size of the whole, when known.</param>
    /// <param name="acceptsRanges">Whether the part can be asked for in pieces.</param>
    /// <param name="owner">Released with the response, such as the upstream connection the body is read from.</param>
    public StreamResponse(string mime, Stream body, long? length, long? rangeStart, long? total, bool acceptsRanges, IAsyncDisposable? owner = null)
    {
        this.owner = owner;
        this.Mime = mime;
        this.Body = body;
        this.Length = length;
        this.RangeStart = rangeStart;
        this.Total = total;
        this.AcceptsRanges = acceptsRanges;
    }

    /// <summary>Gets the type of the body.</summary>
    public string Mime { get; }

    /// <summary>Gets the bytes.</summary>
    public Stream Body { get; }

    /// <summary>Gets the number of bytes in the body, when known.</summary>
    public long? Length { get; }

    /// <summary>Gets the offset of the first byte of a partial answer; <see langword="null"/> when the body is the whole.</summary>
    public long? RangeStart { get; }

    /// <summary>Gets the size of the whole, when known.</summary>
    public long? Total { get; }

    /// <summary>Gets a value indicating whether the part can be asked for in pieces.</summary>
    public bool AcceptsRanges { get; }

    /// <summary>Gets a value indicating whether a range was asked for that starts beyond the end.</summary>
    public bool RangeNotSatisfiable => this.RangeStart is not null && this.Length == 0;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // The body and the connection it comes from were handed over on purpose: the response is their owner.
#pragma warning disable IDISP007
        await this.Body.DisposeAsync().ConfigureAwait(false);
        if (this.owner is not null)
        {
            await this.owner.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning restore IDISP007
    }
}
