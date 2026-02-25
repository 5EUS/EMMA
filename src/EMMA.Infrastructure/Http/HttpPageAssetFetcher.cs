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
        using var request = new HttpRequestMessage(HttpMethod.Get, contentUri);
        request.Headers.UserAgent.ParseAdd("EMMA/1.0");
        request.Headers.Accept.ParseAdd("*/*");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

        return new MediaPageAsset(contentType, payload, DateTimeOffset.UtcNow);
    }
}
