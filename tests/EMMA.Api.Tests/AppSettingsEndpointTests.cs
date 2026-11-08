using System.Net;

using Xunit;

namespace EMMA.Api.Tests;

/// <summary>Tests for the app's remembered choices over HTTP.</summary>
public sealed class AppSettingsEndpointTests
{
    private const string Series = "/v1/app-settings?scope=series:com.example.test:s1";

    /// <summary>Choices are remembered per scope, replaced, forgotten, and private to each client.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task RememberReplaceAndForget() => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse empty = await server.GetAsync(Series).ConfigureAwait(true);
        ApiResponse set = await server.SendAsync(HttpMethod.Put, Series, json: @"{""changes"":[{""key"":""reverse"",""value"":""1""},{""key"":""groups"",""value"":""[\""a\""]""}]}").ConfigureAwait(true);
        ApiResponse replace = await server.SendAsync(HttpMethod.Put, Series, json: @"{""changes"":[{""key"":""reverse"",""value"":""0""},{""key"":""groups""}]}").ConfigureAwait(true);
        ApiResponse read = await server.GetAsync(Series).ConfigureAwait(true);
        ApiResponse bob = await server.GetAsync(Series, ApiTestServer.BobKey).ConfigureAwait(true);

        Assert.Equal("series:com.example.test:s1", empty.Json["scope"]!.GetValue<string>());
        Assert.Empty(empty.Json["values"]!.AsObject());
        Assert.Equal("[\"a\"]", set.Json["values"]!["groups"]!.GetValue<string>());
        Assert.Equal("1", set.Json["values"]!["reverse"]!.GetValue<string>());
        Assert.Single(replace.Json["values"]!.AsObject());
        Assert.Equal("0", read.Json["values"]!["reverse"]!.GetValue<string>());
        Assert.Empty(bob.Json["values"]!.AsObject());
    });

    /// <summary>Reserved and missing scopes, bad keys and malformed bodies are refused.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task BadRequestsAreRefused() => ApiTests.WithServerAsync(async server =>
    {
        const string Body = @"{""changes"":[{""key"":""k"",""value"":""v""}]}";
        ApiResponse reserved = await server.SendAsync(HttpMethod.Put, "/v1/app-settings?scope=emma.history", json: Body).ConfigureAwait(true);
        ApiResponse readReserved = await server.GetAsync("/v1/app-settings?scope=emma.history").ConfigureAwait(true);
        ApiResponse noScope = await server.GetAsync("/v1/app-settings").ConfigureAwait(true);
        ApiResponse badKey = await server.SendAsync(HttpMethod.Put, Series, json: @"{""changes"":[{""key"":""Bad Key"",""value"":""v""}]}").ConfigureAwait(true);
        ApiResponse noKey = await server.SendAsync(HttpMethod.Put, Series, json: @"{""changes"":[{""value"":""v""}]}").ConfigureAwait(true);
        ApiResponse noChanges = await server.SendAsync(HttpMethod.Put, Series, json: "{}").ConfigureAwait(true);
        ApiResponse unknownField = await server.SendAsync(HttpMethod.Put, Series, json: @"{""changes"":[],""extra"":1}").ConfigureAwait(true);
        ApiResponse applied = await server.GetAsync(Series).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, reserved.Status);
        Assert.Equal(HttpStatusCode.BadRequest, readReserved.Status);
        Assert.Equal(HttpStatusCode.BadRequest, noScope.Status);
        Assert.Equal(HttpStatusCode.BadRequest, badKey.Status);
        Assert.Equal(HttpStatusCode.BadRequest, noKey.Status);
        Assert.Equal(HttpStatusCode.BadRequest, noChanges.Status);
        Assert.Equal(HttpStatusCode.BadRequest, unknownField.Status);
        Assert.Empty(applied.Json["values"]!.AsObject());
    });
}
