using EMMA.Api.Contracts;
using EMMA.Api.Http;

namespace EMMA.Api.Endpoints;

/// <summary>The one unauthenticated endpoint: a liveness probe that reveals nothing about the host.</summary>
internal static class HealthEndpoints
{
    /// <summary>The path that is exempt from authentication and rate limiting.</summary>
    public const string Path = "/health";

    /// <summary>Maps the endpoint.</summary>
    /// <param name="routes">The route builder.</param>
    public static void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet(Path, () => ApiResults.Ok(new HealthResponse("ok"), ApiJsonContext.Default.HealthResponse));
}
