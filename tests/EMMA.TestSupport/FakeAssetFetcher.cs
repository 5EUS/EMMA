using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.TestSupport;

/// <summary>A scriptable <see cref="IAssetFetcherPort"/> that counts calls.</summary>
/// <param name="time">The clock that stamps the default payload, like the real fetcher does.</param>
public sealed class FakeAssetFetcher(TimeProvider time) : IAssetFetcherPort
{
    private int calls;

    /// <summary>Gets or sets the answer. By default it returns four bytes of <c>image/png</c>.</summary>
    public Func<RemoteDelivery, Outcome<AssetPayload>> OnFetch { get; set; } =
        _ => Outcome.Success(new AssetPayload("image/png", new byte[] { 1, 2, 3, 4 }, time.GetUtcNow()));

    /// <summary>Gets how many fetches arrived.</summary>
    public int Calls => this.calls;

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
}
