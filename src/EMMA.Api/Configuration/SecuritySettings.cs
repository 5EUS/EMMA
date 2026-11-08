namespace EMMA.Api.Configuration;

/// <summary>Who may call the API and how fast.</summary>
/// <param name="KeyHeader">The request header that carries the API key.</param>
/// <param name="Keys">The configured keys. With none, every request except the health check is refused.</param>
/// <param name="RateLimit">The request limits.</param>
internal sealed record SecuritySettings(string KeyHeader, IReadOnlyList<ApiClientKey> Keys, RateLimitSettings RateLimit);
