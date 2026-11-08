using System.Globalization;
using System.Text.Json.Serialization.Metadata;

namespace EMMA.Api.Http;

/// <summary>A JSON response written with source-generated metadata, with optional extra headers.</summary>
/// <typeparam name="T">The body type.</typeparam>
/// <param name="statusCode">The HTTP status.</param>
/// <param name="value">The body.</param>
/// <param name="typeInfo">How to write the body.</param>
/// <param name="retryAfter">When set, sent as <c>Retry-After</c> in whole seconds, rounded up.</param>
internal sealed class JsonResult<T>(int statusCode, T value, JsonTypeInfo<T> typeInfo, TimeSpan? retryAfter = null) : IResult
{
    /// <inheritdoc/>
    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = statusCode;
        if (retryAfter is { } delay)
        {
            long seconds = (long)Math.Ceiling(Math.Max(0, delay.TotalSeconds));
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        return httpContext.Response.WriteAsJsonAsync(value, typeInfo, contentType: null, httpContext.RequestAborted);
    }
}
