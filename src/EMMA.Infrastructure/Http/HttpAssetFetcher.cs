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
    /// <returns>A handler that does not follow redirects, does not use cookies and does not ask for compressed answers.</returns>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,

        // Nothing is compressed on the way: a ranged answer must describe the bytes the player gets, and a server that
        // compresses whatever accepts compression (Invidious does, media included) would otherwise send a length and a
        // range for the compressed form, then have the handler decompress the body into something they no longer fit.
        AutomaticDecompression = DecompressionMethods.None,
    };

    /// <inheritdoc/>
    public async Task<Outcome<AssetPayload>> FetchAsync(
        RemoteDelivery delivery,
        CapabilityPolicy policy,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        Outcome<HttpResponseMessage> sent = await this.SendFollowingAsync(delivery, policy, range: null, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return Outcome.Failure<AssetPayload>(sent.Error);
        }

        using HttpResponseMessage response = sent.Value;
        return await this.ReadAsync(response, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<Outcome<RemoteResponse>> OpenAsync(
        RemoteDelivery delivery,
        CapabilityPolicy policy,
        ByteRange? range,
        CancellationToken cancellationToken)
    {
        Outcome<HttpResponseMessage> sent = await this.SendFollowingAsync(delivery, policy, range, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return Outcome.Failure<RemoteResponse>(sent.Error);
        }

        HttpResponseMessage response = sent.Value;
        try
        {
            Outcome<RemoteResponse> opened = await this.ToStreamAsync(response, range, cancellationToken).ConfigureAwait(false);
            if (opened.IsSuccess)
            {
                // The stream owns the response from here on.
                response = null!;
            }

            return opened;
        }
        finally
        {
            response?.Dispose();
        }
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

    /// <summary>
    /// Sends the request and follows redirects by hand, so that the plugin's policy applies to every hop and credentials
    /// do not leave the host they were meant for. The caller owns the response it gets back.
    /// </summary>
    private async Task<Outcome<HttpResponseMessage>> SendFollowingAsync(
        RemoteDelivery delivery,
        CapabilityPolicy policy,
        ByteRange? range,
        CancellationToken cancellationToken)
    {
        Uri current = delivery.Url;
        List<HttpHeader> headers = [.. delivery.Headers];
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!policy.AllowsNetwork(current))
            {
                return Outcome.Failure<HttpResponseMessage>(
                    ErrorCode.InvalidArgument,
                    $"The plugin may not reach '{current.Host}'.");
            }

            (HttpResponseMessage? response, string? reason) = await this.TrySendAsync(current, headers, range, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return Outcome.Failure<HttpResponseMessage>(ErrorCode.Unavailable, $"Could not reach '{current.Host}': {reason}");
            }

            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                using (response)
                {
                    Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!string.Equals(next.IdnHost, current.IdnHost, StringComparison.OrdinalIgnoreCase))
                    {
                        headers.RemoveAll(header => CredentialHeaders.Contains(header.Name, StringComparer.OrdinalIgnoreCase));
                    }

                    current = next;
                }

                continue;
            }

            // A handler that follows redirects itself would bypass the per-hop check above.
            if (response.RequestMessage?.RequestUri is { } final && !policy.AllowsNetwork(final))
            {
                response.Dispose();
                return Outcome.Failure<HttpResponseMessage>(
                    ErrorCode.InvalidArgument,
                    $"The plugin may not reach '{final.Host}'.");
            }

            return Outcome.Success(response);
        }

        return Outcome.Failure<HttpResponseMessage>(ErrorCode.Unavailable, "Too many redirects.");
    }

    private async Task<(HttpResponseMessage? Response, string? Reason)> TrySendAsync(
        Uri url,
        IReadOnlyList<HttpHeader> headers,
        ByteRange? range,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        foreach (HttpHeader header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        if (range is not null)
        {
            request.Headers.Range = new RangeHeaderValue(range.Start, range.Length is { } length ? range.Start + length - 1 : null);
        }

        try
        {
            HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return (response, null);
        }
        catch (HttpRequestException exception)
        {
            // The innermost message names the real cause (a name that did not resolve, a certificate that did not validate).
            Exception cause = exception;
            while (cause.InnerException is { } inner)
            {
                cause = inner;
            }

            return (null, cause.Message);
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

    // The response and the streams over it are handed to the RemoteResponse that is returned, which releases them; the analyzers cannot follow that.
#pragma warning disable CA2000, IDISP001, IDISP007
    private async Task<Outcome<RemoteResponse>> ToStreamAsync(HttpResponseMessage response, ByteRange? range, CancellationToken cancellationToken)
    {
        string mime = response.Content.Headers.ContentType?.MediaType ?? DefaultMime;
        bool acceptsRanges = response.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            long? total = response.Content.Headers.ContentRange?.Length;
            return Outcome.Success(new RemoteResponse(mime, 0, range?.Start, total, acceptsRanges: true, Stream.Null));
        }

        if (!response.IsSuccessStatusCode)
        {
            return Outcome.Failure<RemoteResponse>(ToError(response, time.GetUtcNow()));
        }

        Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            ContentRangeHeaderValue? served = response.Content.Headers.ContentRange;
            long start = served?.From ?? range?.Start ?? 0;

            // The length header is gone when the handler decompressed the body, or the upstream sent it in chunks; the range it
            // served says how long this part is, and a player needs that to be told where the part ends.
            long? partLength = response.Content.Headers.ContentLength ?? (served is { From: { } from, To: { } to } ? to - from + 1 : null);
            return Outcome.Success(new RemoteResponse(mime, partLength, start, served?.Length, acceptsRanges: true, body, response));
        }

        long? whole = response.Content.Headers.ContentLength;
        if (range is null)
        {
            return Outcome.Success(new RemoteResponse(mime, whole, rangeStart: null, whole, acceptsRanges, body, response));
        }

        // The upstream ignored the range and sent everything: skip to the start and cut the body where the range ends.
        long remaining = range.Start;
        byte[] scratch = new byte[81920];
        while (remaining > 0)
        {
            int read = await body.ReadAsync(scratch.AsMemory(0, (int)Math.Min(scratch.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                await body.DisposeAsync().ConfigureAwait(false);
                return Outcome.Success(new RemoteResponse(mime, 0, range.Start, whole, acceptsRanges: false, Stream.Null, response));
            }

            remaining -= read;
        }

        long? available = whole is { } size ? Math.Max(size - range.Start, 0) : null;
        long? length = range.Length is { } wanted ? Math.Min(wanted, available ?? wanted) : available;
        Stream limited = length is { } count ? new LimitedStream(body, count) : body;
        return Outcome.Success(new RemoteResponse(mime, length, range.Start, whole, acceptsRanges: false, limited, response));
    }
#pragma warning restore CA2000, IDISP001, IDISP007
}
