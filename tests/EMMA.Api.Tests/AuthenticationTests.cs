using System.Net;

using Xunit;

namespace EMMA.Api.Tests;

/// <summary>Tests that the API refuses by default and lets exactly the right requests through.</summary>
public sealed class AuthenticationTests
{
    /// <summary>The health check is the only endpoint that needs no key.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task HealthIsAnonymous() => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse response = await server.GetAsync("/health", key: null).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("ok", response.Json["status"]?.GetValue<string>());
    });

    /// <summary>A request without a key, or with a wrong one, is refused with a JSON error and a challenge.</summary>
    /// <param name="key">The key to send.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("ALICE-TEST-KEY")]
    public Task MissingOrWrongKeyIsRefused(string? key) => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse response = await server.GetAsync("/v1/plugins", key).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.Equal("unauthenticated", response.ErrorCode);
        Assert.Equal("ApiKey", response.Header("WWW-Authenticate"));
    });

    /// <summary>Authentication is the default for every path, including ones that do not exist, so probing reveals nothing.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task UnknownPathsAreRefusedBeforeTheyAreRouted() => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse anonymous = await server.GetAsync("/no/such/path", key: null).ConfigureAwait(true);
        ApiResponse authenticated = await server.GetAsync("/no/such/path").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
        Assert.Equal(HttpStatusCode.NotFound, authenticated.Status);
    });

    /// <summary>The exemption is exact: a path that only resembles the health check still needs a key.</summary>
    /// <param name="path">The lookalike path.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("/health/")]
    [InlineData("/Health")]
    [InlineData("/health/../v1/plugins")]
    public Task HealthExemptionIsExact(string path) => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse response = await server.GetAsync(path, key: null).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
    });

    /// <summary>A valid key reaches the endpoints.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ValidKeyIsAccepted() => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse response = await server.GetAsync("/v1/plugins").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.Status);
    });

    /// <summary>The key is a header secret, not a query parameter.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task KeyInTheQueryStringDoesNotCount() => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse response = await server.GetAsync($"/v1/plugins?x-api-key={ApiTestServer.AliceKey}", key: null).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
    });

    /// <summary>Responses never name the server software.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ServerHeaderIsNotSent() => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse response = await server.GetAsync("/health", key: null).ConfigureAwait(true);

        Assert.Null(response.Header("Server"));
    });
}
