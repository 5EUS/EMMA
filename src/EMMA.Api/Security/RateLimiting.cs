using System.Threading.RateLimiting;

using EMMA.Api.Configuration;
using EMMA.Api.Http;

using Microsoft.AspNetCore.RateLimiting;

namespace EMMA.Api.Security;

/// <summary>
/// The API's request limits. Both run after authentication and are keyed by the authenticated client, never by
/// anything the caller can make up, so inventing a new key or header does not buy a fresh allowance. Requests
/// without a client (only the health check gets that far) are not limited.
/// </summary>
internal static class RateLimiting
{
    private const string Anonymous = "anonymous";

    /// <summary>Applies the limits.</summary>
    /// <param name="options">The limiter options being built.</param>
    /// <param name="settings">The configured limits.</param>
    public static void Configure(RateLimiterOptions options, RateLimitSettings settings)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // The chained limiter owns the two limiters it is given and disposes them with itself, which the disposal analyzer cannot see.
#pragma warning disable IDISP004
        options.GlobalLimiter = PartitionedRateLimiter.CreateChained(Concurrency(settings), PerClient(settings));
#pragma warning restore IDISP004

        options.OnRejected = (context, cancellationToken) =>
        {
            TimeSpan? retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan delay) ? delay : null;
            return new ValueTask(ApiResults
                .Error(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many requests; slow down.", retryAfter)
                .ExecuteAsync(context.HttpContext));
        };
    }

    private static PartitionedRateLimiter<HttpContext> Concurrency(RateLimitSettings settings) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context => context.GetClient() is null
            ? RateLimitPartition.GetNoLimiter(Anonymous)
            : RateLimitPartition.GetConcurrencyLimiter("all", _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = settings.GlobalConcurrency,
                QueueLimit = settings.GlobalQueue,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));

    private static PartitionedRateLimiter<HttpContext> PerClient(RateLimitSettings settings) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context => context.GetClient() is { } client
            ? RateLimitPartition.GetFixedWindowLimiter(client.ClientId, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = settings.PerClientPermits,
                Window = settings.PerClientWindow,
                QueueLimit = 0,
            })
            : RateLimitPartition.GetNoLimiter(Anonymous));
}
