using System.Globalization;
using System.Net.Http.Headers;

namespace EMMA.Api.Http;

/// <summary>
/// Sends asset bytes. The type and bytes come from a plugin, so the response is locked down: the type is
/// re-parsed (a malformed one becomes <c>application/octet-stream</c>), sniffing is off, and a sandboxing
/// policy stops a browser from running a plugin-supplied page with this API's authority.
/// </summary>
/// <param name="mime">The plugin's declared type.</param>
/// <param name="bytes">The content.</param>
/// <param name="range">For a partial response, where the bytes sit in the whole; otherwise <see langword="null"/>.</param>
/// <param name="acceptsRanges">Whether the asset supports byte ranges.</param>
internal sealed class AssetContentResult(string mime, ReadOnlyMemory<byte> bytes, ContentRange? range, bool acceptsRanges) : IResult
{
    private const string FallbackMime = "application/octet-stream";

    /// <inheritdoc/>
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        HttpResponse response = httpContext.Response;
        response.StatusCode = range is null ? StatusCodes.Status200OK : StatusCodes.Status206PartialContent;
        response.ContentType = MediaTypeHeaderValue.TryParse(mime, out MediaTypeHeaderValue? parsed) ? parsed.ToString() : FallbackMime;
        response.ContentLength = bytes.Length;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        response.Headers.CacheControl = "private";
        if (acceptsRanges)
        {
            response.Headers.AcceptRanges = "bytes";
        }

        if (range is not null)
        {
            string total = range.Total is { } known ? known.ToString(CultureInfo.InvariantCulture) : "*";
            response.Headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes {range.Start}-{range.Start + bytes.Length - 1}/{total}");
        }

        await response.Body.WriteAsync(bytes, httpContext.RequestAborted).ConfigureAwait(false);
    }
}
