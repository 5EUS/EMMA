using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// Fetches a remote asset on the plugin's behalf. Implementations must enforce the
/// policy on every redirect hop, not just the first URL, and stop at the size limit.
/// </summary>
public interface IAssetFetcherPort
{
    /// <summary>Fetches the resource.</summary>
    /// <param name="delivery">The URL and headers to use.</param>
    /// <param name="policy">The plugin's capabilities; every hop must be allowed.</param>
    /// <param name="maxBytes">The most bytes to accept.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The payload, or a failure for a blocked host, oversized body or upstream error.</returns>
    Task<Outcome<AssetPayload>> FetchAsync(
        RemoteDelivery delivery,
        CapabilityPolicy policy,
        long maxBytes,
        CancellationToken cancellationToken);
}
