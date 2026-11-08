using Microsoft.AspNetCore.Http.Features;

namespace EMMA.Api.Security;

/// <summary>Carries the authenticated client from the authentication step to the handlers and the rate limiter.</summary>
internal static class ClientFeatureExtensions
{
    /// <summary>Records who the request is from.</summary>
    /// <param name="context">The request.</param>
    /// <param name="client">The authenticated client.</param>
    public static void SetClient(this HttpContext context, ApiClient client) => context.Features.Set(client);

    /// <summary>Gets who the request is from.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The client, or <see langword="null"/> for an unauthenticated request (only the health check passes).</returns>
    public static ApiClient? GetClient(this HttpContext context) => context.Features.Get<ApiClient>();

    /// <summary>Gets who the request is from, for handlers that authentication guards.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The client.</returns>
    public static ApiClient RequireClient(this HttpContext context) => context.Features.GetRequiredFeature<ApiClient>();
}
