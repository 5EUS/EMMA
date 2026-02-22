using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.Http;

/// <summary>
/// Fetches page assets over HTTP.
/// </summary>
public sealed class HttpPageAssetFetcher(HttpClient? httpClient = null) : IPageAssetFetcherPort
{
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient();

    public async Task<MediaPageAsset> FetchAsync(Uri contentUri, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(contentUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

        return new MediaPageAsset(contentType, payload, DateTimeOffset.UtcNow);
    }
}
