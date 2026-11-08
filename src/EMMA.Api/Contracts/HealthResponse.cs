namespace EMMA.Api.Contracts;

/// <summary>The body of the health check.</summary>
/// <param name="Status">Always <c>ok</c> while the process serves requests.</param>
internal sealed record HealthResponse(
    string Status);
