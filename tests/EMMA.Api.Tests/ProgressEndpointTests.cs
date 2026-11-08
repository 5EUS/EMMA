using System.Net;

using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Api.Tests;

/// <summary>Tests that progress is recorded per authenticated client and validated.</summary>
public sealed class ProgressEndpointTests
{
    private const string Chapter = "/v1/progress?ref=com.example.test:c1";

    /// <summary>Progress that is set can be read back, with one shape whatever the unit.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ProgressRoundTrips() => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse set = await server.SendAsync(HttpMethod.Put, Chapter, json: @"{""unit"":""page"",""value"":7,""total"":20}").ConfigureAwait(true);
        ApiResponse get = await server.GetAsync(Chapter).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, set.Status);
        Assert.Equal("com.example.test:c1", get.Json["node"]!.GetValue<string>());
        Assert.Equal("page", get.Json["unit"]!.GetValue<string>());
        Assert.Equal(7, get.Json["value"]!.GetValue<double>());
        Assert.Equal(20, get.Json["total"]!.GetValue<double>());
        Assert.False(get.Json["completed"]!.GetValue<bool>());
        Assert.NotNull(get.Json["updated_at"]);
    });

    /// <summary>Progress in milliseconds (video) is stored exactly like progress in pages (manga).</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task AnyUnitIsAccepted() => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse set = await server.SendAsync(HttpMethod.Put, "/v1/progress?ref=com.example.test:e1", json: @"{""unit"":""ms"",""value"":93500,""completed"":true}").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, set.Status);
        Assert.True(set.Json["completed"]!.GetValue<bool>());
        Assert.Equal("ms", set.Json["unit"]!.GetValue<string>());
    });

    /// <summary>One client cannot read or overwrite another's progress, and there is no way to name another user.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ProgressBelongsToTheCaller() => ApiTests.WithServerAsync(async server =>
    {
        await server.SendAsync(HttpMethod.Put, Chapter, json: @"{""unit"":""page"",""value"":3}").ConfigureAwait(true);
        await server.SendAsync(HttpMethod.Put, Chapter, ApiTestServer.BobKey, @"{""unit"":""page"",""value"":9}").ConfigureAwait(true);

        ApiResponse alice = await server.GetAsync(Chapter).ConfigureAwait(true);
        ApiResponse bob = await server.GetAsync(Chapter, ApiTestServer.BobKey).ConfigureAwait(true);
        ApiResponse spoof = await server.GetAsync($"{Chapter}&user=bob").ConfigureAwait(true);

        Assert.Equal(3, alice.Json["value"]!.GetValue<double>());
        Assert.Equal(9, bob.Json["value"]!.GetValue<double>());
        Assert.Equal(HttpStatusCode.OK, spoof.Status);
        Assert.Equal(3, spoof.Json["value"]!.GetValue<double>());
    });

    /// <summary>Reading progress that was never set is not found.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task MissingProgressIsNotFound() => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse response = await server.GetAsync(Chapter).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        Assert.Equal("not_found", response.ErrorCode);
    });

    /// <summary>Invalid positions are refused with a reason.</summary>
    /// <param name="body">The request body.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData(@"{""value"":3}")]
    [InlineData(@"{""unit"":""page""}")]
    [InlineData(@"{""unit"":""Page"",""value"":3}")]
    [InlineData(@"{""unit"":""page"",""value"":-1}")]
    [InlineData(@"{""unit"":""page"",""value"":5,""total"":2}")]
    [InlineData(@"{""unit"":""page"",""value"":""three""}")]
    [InlineData(@"{""unit"":""page"",""value"":1,""user"":""bob""}")]
    public Task InvalidProgressIsRefused(string body) => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse response = await server.SendAsync(HttpMethod.Put, Chapter, json: body).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal("invalid_argument", response.ErrorCode);
    });

    /// <summary>Continue finds the latest progress anywhere beneath a series.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ContinueFindsProgressBeneathASeries() => ApiTests.WithServerAsync(async server =>
    {
        NodeRef series = TestData.Ref("s1");
        await server.RememberAsync(
            new Node(series, NodeKind.Create("series"), "S", null, null, []),
            new Node(TestData.Ref("c1"), NodeKind.Create("chapter"), "C1", series, null, [])).ConfigureAwait(true);
        await server.SendAsync(HttpMethod.Put, Chapter, json: @"{""unit"":""page"",""value"":4}").ConfigureAwait(true);

        ApiResponse found = await server.GetAsync("/v1/progress/continue?ref=com.example.test:s1").ConfigureAwait(true);
        ApiResponse other = await server.GetAsync("/v1/progress/continue?ref=com.example.test:s1", ApiTestServer.BobKey).ConfigureAwait(true);

        Assert.Equal("com.example.test:c1", found.Json["node"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, other.Status);
    });

    /// <summary>Recent progress lists the caller's own, newest first, within the limit.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task RecentIsPerCallerAndLimited() => ApiTests.WithServerAsync(async server =>
    {
        await server.SendAsync(HttpMethod.Put, "/v1/progress?ref=com.example.test:a", json: @"{""unit"":""page"",""value"":1}").ConfigureAwait(true);
        await server.SendAsync(HttpMethod.Put, "/v1/progress?ref=com.example.test:b", json: @"{""unit"":""page"",""value"":1}").ConfigureAwait(true);
        await server.SendAsync(HttpMethod.Put, "/v1/progress?ref=com.example.test:z", ApiTestServer.BobKey, @"{""unit"":""page"",""value"":1}").ConfigureAwait(true);

        ApiResponse all = await server.GetAsync("/v1/progress/recent").ConfigureAwait(true);
        ApiResponse one = await server.GetAsync("/v1/progress/recent?limit=1").ConfigureAwait(true);
        ApiResponse tooMany = await server.GetAsync("/v1/progress/recent?limit=201").ConfigureAwait(true);

        Assert.Equal(2, all.Json["items"]!.AsArray().Count);
        Assert.Single(one.Json["items"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.Status);
    });
}
