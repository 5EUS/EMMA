using EMMA.Domain;

namespace EMMA.Application.Streaming;

/// <summary>
/// Reads an asset only the plugin can read, piece by piece as the player consumes it, so a large file never has to fit in
/// memory. The first piece is read by the caller (it tells the total and surfaces an error before any header is sent).
/// </summary>
internal sealed class ProxiedStream : Stream
{
    private readonly Func<ByteRange, CancellationToken, Task<Outcome<Chunk>>> read;
    private readonly int chunkBytes;
    private readonly long? end;
    private ReadOnlyMemory<byte> current;
    private long position;
    private bool finished;

    /// <summary>Initializes a new instance of the <see cref="ProxiedStream"/> class.</summary>
    /// <param name="read">Reads a range from the plugin.</param>
    /// <param name="first">The piece at <paramref name="start"/>, already read.</param>
    /// <param name="start">Where the stream begins in the asset.</param>
    /// <param name="end">Where it stops (exclusive), or <see langword="null"/> for the end of the asset.</param>
    /// <param name="chunkBytes">The size of each further read.</param>
    public ProxiedStream(Func<ByteRange, CancellationToken, Task<Outcome<Chunk>>> read, ReadOnlyMemory<byte> first, long start, long? end, int chunkBytes)
    {
        this.read = read;
        this.current = end is { } stop && start + first.Length > stop ? first[..(int)(stop - start)] : first;
        this.position = start;
        this.end = end;
        this.chunkBytes = chunkBytes;
        this.finished = this.current.IsEmpty;
    }

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
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (this.current.IsEmpty && !this.finished)
        {
            await this.FillAsync(cancellationToken).ConfigureAwait(false);
        }

        int count = Math.Min(buffer.Length, this.current.Length);
        this.current[..count].CopyTo(buffer);
        this.current = this.current[count..];
        this.position += count;
        return count;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private async Task FillAsync(CancellationToken cancellationToken)
    {
        long length = this.end is { } stop ? Math.Min(this.chunkBytes, stop - this.position) : this.chunkBytes;
        if (length <= 0)
        {
            this.finished = true;
            return;
        }

        Outcome<Chunk> chunk = await this.read(new ByteRange(this.position, length), cancellationToken).ConfigureAwait(false);
        if (!chunk.IsSuccess)
        {
            throw new IOException(chunk.Error.Message);
        }

        this.current = chunk.Value.Bytes;
        this.finished = this.current.IsEmpty;
    }
}
