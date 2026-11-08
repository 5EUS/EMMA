using EMMA.Api.Endpoints;
using EMMA.Api.Http;

using Microsoft.Extensions.Primitives;

namespace EMMA.Api.Security;

/// <summary>
/// Authenticates every request before anything else looks at it: a request without a valid key never reaches
/// the rate limiter or an endpoint. The default is to refuse; the health check is the only exemption, and it is
/// matched exactly, so a path that merely looks like it (a trailing slash, a different case) is refused.
/// </summary>
/// <param name="validator">Checks keys.</param>
/// <param name="headerName">The header the key is read from.</param>
internal sealed class ApiAuthenticator(ApiKeyValidator validator, string headerName)
{
    /// <summary>Authenticates a request and passes it on, or answers 401.</summary>
    /// <param name="context">The request.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (string.Equals(context.Request.Path.Value, HealthEndpoints.Path, StringComparison.Ordinal))
        {
            return next(context);
        }

        // One header exactly: a repeated header is ambiguous, and ambiguity is refused rather than resolved.
        StringValues presented = context.Request.Headers[headerName];
        ApiClient? client = presented.Count == 1 ? validator.Validate(presented[0]) : null;
        if (client is null)
        {
            context.Response.Headers.WWWAuthenticate = "ApiKey";
            return ApiResults.Error(StatusCodes.Status401Unauthorized, "unauthenticated", "A valid API key is required.").ExecuteAsync(context);
        }

        context.SetClient(client);
        return next(context);
    }
}
