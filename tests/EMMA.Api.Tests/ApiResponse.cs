using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace EMMA.Api.Tests;

/// <summary>A response read fully, so assertions need no streams.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Headers">The response headers.</param>
/// <param name="ContentHeaders">The content headers.</param>
/// <param name="Body">The body bytes.</param>
internal sealed record ApiResponse(HttpStatusCode Status, HttpResponseHeaders Headers, HttpContentHeaders ContentHeaders, byte[] Body)
{
    /// <summary>Gets the body parsed as JSON.</summary>
    public JsonNode Json => JsonNode.Parse(this.Body)!;

    /// <summary>Gets the body as text.</summary>
    public string Text => Encoding.UTF8.GetString(this.Body);

    /// <summary>Gets the error code of a failed response.</summary>
    public string? ErrorCode => this.Json["error"]?["code"]?.GetValue<string>();

    /// <summary>Reads one header value.</summary>
    /// <param name="name">The header name.</param>
    /// <returns>The value, or <see langword="null"/> when absent.</returns>
    public string? Header(string name) =>
        this.Headers.TryGetValues(name, out IEnumerable<string>? values) || this.ContentHeaders.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : null;
}
