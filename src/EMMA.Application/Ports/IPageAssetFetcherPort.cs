using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// Fetches page assets from a remote source.
/// </summary>
public interface IPageAssetFetcherPort
{
    /// <summary>
    /// Retrieves the raw page asset payload.
    /// </summary>
    Task<MediaPageAsset> FetchAsync(Uri contentUri, CancellationToken cancellationToken);
}
