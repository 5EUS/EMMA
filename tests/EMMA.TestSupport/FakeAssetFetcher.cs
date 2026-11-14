using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.TestSupport;

/// <summary>A scriptable <see cref="IAssetFetcherPort"/> that counts calls.</summary>
/// <param name="time">The clock that stamps the default payload, like the real fetcher does.</param>
public sealed class FakeAssetFetcher(TimeProvider time) : IAssetFetcherPort
{
    private readonly List<(RemoteDelivery Delivery, ByteRange? Range)> opens = [];
    private int calls;

    /// <summary>Gets or sets the answer. By default it returns four bytes of <c>image/png</c>.</summary>
    public Func<RemoteDelivery, Outcome<AssetPayload>> OnFetch { get; set; } =
        _ => Outcome.Success(new AssetPayload("image/png", new byte[] { 1, 2, 3, 4 }, time.GetUtcNow()));

    /// <summary>Gets how many fetches arrived.</summary>
    public int Calls => this.calls;

    /// <summary>Gets the requests <see cref="OpenAsync"/> received, in order.</summary>
    public IReadOnlyList<(RemoteDelivery Delivery, ByteRange? Range)> Opens
    {
        get
        {
            lock (this.opens)
            {
                return [.. this.opens];
            }
        }
    }

    /// <inheritdoc/>
    public Task<Outcome<AssetPayload>> FetchAsync(
        RemoteDelivery delivery,
        CapabilityPolicy policy,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref this.calls);
        return Task.FromResult(this.OnFetch(delivery));
    }

    /// <inheritdoc/>
    public Task<Outcome<RemoteResponse>> OpenAsync(
        RemoteDelivery delivery,
        CapabilityPolicy policy,
        ByteRange? range,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref this.calls);
        lock (this.opens)
        {
            this.opens.Add((delivery, range));
        }

        Outcome<AssetPayload> payload = this.OnFetch(delivery);
        if (!payload.IsSuccess)
        {
            return Task.FromResult(Outcome.Failure<RemoteResponse>(payload.Error));
        }

        ReadOnlyMemory<byte> bytes = payload.Value.Payload;
        if (range is null)
        {
            return Task.FromResult(Outcome.Success(Response(payload.Value.Mime, bytes, rangeStart: null, bytes.Length)));
        }

        int start = (int)Math.Min(range.Start, bytes.Length);
        int length = (int)Math.Min(range.Length ?? bytes.Length, bytes.Length - start);
        return Task.FromResult(Outcome.Success(Response(payload.Value.Mime, bytes.Slice(start, length), range.Start, bytes.Length)));
    }

    // The caller owns the response and its body.
#pragma warning disable CA2000, IDISP004
    private static RemoteResponse Response(string mime, ReadOnlyMemory<byte> bytes, long? rangeStart, long total) =>
        new(mime, bytes.Length, rangeStart, total, acceptsRanges: true, new MemoryStream(bytes.ToArray(), writable: false));
#pragma warning restore CA2000, IDISP004
}
