using System.Globalization;
using System.Net.Http.Headers;

using EMMA.Application.Streaming;

namespace EMMA.Api.Http;

/// <summary>
/// Sends part of a stream a player asked for. Like asset bytes, what comes back was chosen by a plugin, so the type is
/// re-parsed, sniffing is off and a sandboxing policy stops a browser from running it with this API's authority.
/// </summary>
/// <param name="response">The answer; it is disposed when the body has been sent.</param>
/// <param name="headOnly">Whether to send the headers and no body.</param>
internal sealed class StreamResult(StreamResponse response, bool headOnly) : IResult
{
    private const string FallbackMime = "application/octet-stream";

    /// <inheritdoc/>
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        // The result owns the response from the moment the endpoint returned it.
#pragma warning disable IDISP007
        await using (response.ConfigureAwait(false))
#pragma warning restore IDISP007
        {
            HttpResponse http = httpContext.Response;
            if (response.RangeNotSatisfiable)
            {
                http.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                http.Headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes */{response.Total?.ToString(CultureInfo.InvariantCulture) ?? "*"}");
                return;
            }

            http.StatusCode = response.RangeStart is null ? StatusCodes.Status200OK : StatusCodes.Status206PartialContent;
            http.ContentType = MediaTypeHeaderValue.TryParse(response.Mime, out MediaTypeHeaderValue? parsed) ? parsed.ToString() : FallbackMime;
            http.ContentLength = response.Length;
            http.Headers.XContentTypeOptions = "nosniff";
            http.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            http.Headers.CacheControl = "no-store";
            if (response.AcceptsRanges)
            {
                http.Headers.AcceptRanges = "bytes";
            }

            if (response.RangeStart is { } start)
            {
                string total = response.Total?.ToString(CultureInfo.InvariantCulture) ?? "*";
                http.Headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes {start}-{start + (response.Length ?? 1) - 1}/{total}");
            }

            if (!headOnly)
            {
                await response.Body.CopyToAsync(http.Body, httpContext.RequestAborted).ConfigureAwait(false);
            }
        }
    }
}
