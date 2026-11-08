using System.Net;

using Xunit;

namespace EMMA.Api.Tests;

/// <summary>Tests that rate limiting runs after authentication and is keyed by who the caller really is.</summary>
public sealed class RateLimitTests
{
    /// <summary>Past the allowance a client is told to wait, with a machine-readable code and a Retry-After.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ExceedingTheAllowanceIsRefused() => ApiTests.WithServerAsync(
        async server =>
        {
            await server.GetAsync("/v1/plugins").ConfigureAwait(true);
            await server.GetAsync("/v1/plugins").ConfigureAwait(true);
            ApiResponse third = await server.GetAsync("/v1/plugins").ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.TooManyRequests, third.Status);
            Assert.Equal("rate_limited", third.ErrorCode);
            Assert.NotNull(third.Header("Retry-After"));
            Assert.True(long.Parse(third.Header("Retry-After")!, System.Globalization.CultureInfo.InvariantCulture) > 0);
        },
        perClientPermits: 2);

    /// <summary>One client using up its allowance does not slow another.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ClientsHaveSeparateAllowances() => ApiTests.WithServerAsync(
        async server =>
        {
            await server.GetAsync("/v1/plugins").ConfigureAwait(true);
            ApiResponse aliceBlocked = await server.GetAsync("/v1/plugins").ConfigureAwait(true);
            ApiResponse bob = await server.GetAsync("/v1/plugins", ApiTestServer.BobKey).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.TooManyRequests, aliceBlocked.Status);
            Assert.Equal(HttpStatusCode.OK, bob.Status);
        },
        perClientPermits: 1);

    /// <summary>Inventing keys buys nothing: they are refused before the limiter, and they do not spend a real client's allowance.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task FakeKeysNeitherGetABucketNorSpendOne() => ApiTests.WithServerAsync(
        async server =>
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                ApiResponse fake = await server.GetAsync("/v1/plugins", $"fake-{attempt}").ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.Unauthorized, fake.Status);
            }

            ApiResponse first = await server.GetAsync("/v1/plugins").ConfigureAwait(true);
            ApiResponse second = await server.GetAsync("/v1/plugins").ConfigureAwait(true);
            ApiResponse afterFake = await server.GetAsync("/v1/plugins", "fake-again").ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.OK, first.Status);
            Assert.Equal(HttpStatusCode.TooManyRequests, second.Status);
            Assert.Equal(HttpStatusCode.Unauthorized, afterFake.Status);
        },
        perClientPermits: 1);

    /// <summary>A caller cannot pick its own bucket with a header: the key decides, not anything else in the request.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task SpoofedIdentityHeadersDoNotChangeTheBucket() => ApiTests.WithServerAsync(
        async server =>
        {
            await server.GetAsync("/v1/plugins").ConfigureAwait(true);
            ApiResponse spoofed = await server.SendAsync(
                HttpMethod.Get,
                "/v1/plugins",
                configure: request =>
                {
                    request.Headers.Add("X-Forwarded-For", "203.0.113.9");
                    request.Headers.Add("X-Client-Id", "someone-else");
                }).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.TooManyRequests, spoofed.Status);
        },
        perClientPermits: 1);

    /// <summary>The health check is never limited, so a probe cannot be starved by traffic.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task HealthIsNotLimited() => ApiTests.WithServerAsync(
        async server =>
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                ApiResponse health = await server.GetAsync("/health", key: null).ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.OK, health.Status);
            }
        },
        perClientPermits: 1);
}
