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

    /// <summary>
    /// Opens the resource and hands back its body to read as it arrives, for media that is too large to buffer. The same
    /// rules apply as for <see cref="FetchAsync"/>, except that there is no size limit: the caller decides how much to read.
    /// </summary>
    /// <param name="delivery">The URL and headers to use.</param>
    /// <param name="policy">The plugin's capabilities; every hop must be allowed.</param>
    /// <param name="range">The bytes wanted, or <see langword="null"/> for all of them. An upstream that ignores the range is skipped forward to it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The open response, or a failure for a blocked host or an upstream error. A range beyond the end is an empty body with a total.</returns>
    Task<Outcome<RemoteResponse>> OpenAsync(
        RemoteDelivery delivery,
        CapabilityPolicy policy,
        ByteRange? range,
        CancellationToken cancellationToken);
}
