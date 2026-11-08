using System.Net;
using System.Net.Http.Headers;

using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.Http;

/// <summary>
/// Fetches remote assets over HTTP on a plugin's behalf. The plugin's network policy
/// applies to the first URL <em>and to every redirect hop</em> (a redirect to a host the
/// plugin may not reach is how a permitted host would otherwise be turned into a proxy),
/// credentials are dropped when a redirect leaves the original host, and the body stops
/// at the size limit.
/// </summary>
/// <param name="client">The client to use. Its handler must not follow redirects (see <see cref="CreateHandler"/>): this class follows them itself so that each hop can be checked.</param>
/// <param name="time">The time source.</param>
public sealed class HttpAssetFetcher(HttpClient client, TimeProvider time) : IAssetFetcherPort
{
    private const int MaxRedirects = 5;
    private const string DefaultMime = "application/octet-stream";

    private static readonly string[] CredentialHeaders = ["Authorization", "Cookie", "Proxy-Authorization"];

    /// <summary>Creates a handler configured the way this fetcher requires.</summary>
    /// <returns>A handler that does not follow redirects and does not use cookies.</returns>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    };

    /// <inheritdoc/>
    public async Task<Outcome<AssetPayload>> FetchAsync(
        RemoteDelivery delivery,
        CapabilityPolicy policy,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        Uri current = delivery.Url;
        List<HttpHeader> headers = [.. delivery.Headers];
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!policy.AllowsNetwork(current))
            {
                return Outcome.Failure<AssetPayload>(
                    ErrorCode.InvalidArgument,
                    $"The plugin may not reach '{current.Host}'.");
            }

            using HttpResponseMessage? response = await this.TrySendAsync(current, headers, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return Outcome.Failure<AssetPayload>(ErrorCode.Unavailable, $"Could not reach '{current.Host}'.");
            }

            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!string.Equals(next.IdnHost, current.IdnHost, StringComparison.OrdinalIgnoreCase))
                {
                    headers.RemoveAll(header => CredentialHeaders.Contains(header.Name, StringComparer.OrdinalIgnoreCase));
                }

                current = next;
                continue;
            }

            // A handler that follows redirects itself would bypass the per-hop check above.
            if (response.RequestMessage?.RequestUri is { } final && !policy.AllowsNetwork(final))
            {
                return Outcome.Failure<AssetPayload>(
                    ErrorCode.InvalidArgument,
                    $"The plugin may not reach '{final.Host}'.");
            }

            return await this.ReadAsync(response, maxBytes, cancellationToken).ConfigureAwait(false);
        }

        return Outcome.Failure<AssetPayload>(ErrorCode.Unavailable, "Too many redirects.");
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static PluginError ToError(HttpResponseMessage response, DateTimeOffset now)
    {
        int status = (int)response.StatusCode;
        return status switch
        {
            404 or 410 => new PluginError(ErrorCode.NotFound, "The asset does not exist."),
            401 or 403 => new PluginError(ErrorCode.Unauthorized, "The upstream refused access to the asset."),
            429 => new PluginError(ErrorCode.RateLimited, "The upstream asked for slower requests.", RetryAfterOf(response, now)),
            408 or >= 500 => new PluginError(ErrorCode.Unavailable, $"The upstream failed with status {status}."),
            _ => new PluginError(ErrorCode.InvalidArgument, $"The upstream rejected the request with status {status}."),
        };
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response, DateTimeOffset now)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date && date > now ? date - now : null;
    }

    private async Task<HttpResponseMessage?> TrySendAsync(
        Uri url,
        IReadOnlyList<HttpHeader> headers,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        foreach (HttpHeader header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        try
        {
            return await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<Outcome<AssetPayload>> ReadAsync(
        HttpResponseMessage response,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return Outcome.Failure<AssetPayload>(ToError(response, time.GetUtcNow()));
        }

        if (response.Content.Headers.ContentLength > maxBytes)
        {
            return Outcome.Failure<AssetPayload>(ErrorCode.InvalidArgument, "The asset is larger than the size limit.");
        }

        using MemoryStream buffer = new();
        Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return Outcome.Failure<AssetPayload>(ErrorCode.InvalidArgument, "The asset is larger than the size limit.");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        string mime = response.Content.Headers.ContentType?.MediaType ?? DefaultMime;
        return Outcome.Success(new AssetPayload(mime, buffer.ToArray(), time.GetUtcNow()));
    }
}
