// This stream wraps another and closes it with itself, like every stream that decorates one.
#pragma warning disable IDISP007
namespace EMMA.Infrastructure.Http;

/// <summary>A read-only view of the first bytes of a stream.</summary>
/// <param name="inner">The stream.</param>
/// <param name="limit">How many bytes may be read.</param>
internal sealed class LimitedStream(Stream inner, long limit) : Stream
{
    private long remaining = limit;

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        if (this.remaining <= 0)
        {
            return 0;
        }

        int read = inner.Read(buffer[..(int)Math.Min(buffer.Length, this.remaining)]);
        this.remaining -= read;
        return read;
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (this.remaining <= 0)
        {
            return 0;
        }

        int read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, this.remaining)], cancellationToken).ConfigureAwait(false);
        this.remaining -= read;
        return read;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
