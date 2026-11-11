using System.Net;

using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Api.Tests;

/// <summary>Tests refreshing saved nodes over HTTP.</summary>
public sealed class RefreshEndpointTests
{
    /// <summary>A refresh reports the children that appeared, a failing node is listed beside the ones that worked, and each client refreshes its own library.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task RefreshReportsNewChildrenAndFailures() => ApiTests.WithServerAsync(async server =>
    {
        List<string> chapters = ["c1"];
        server.Gateway.OnChildren = (_, parent, _) => string.Equals(parent?.Value, "bad", StringComparison.Ordinal)
            ? Outcome.Failure<PluginNodePage>(ErrorCode.Unavailable, "down")
            : Outcome.Success(TestData.RawPage(null, [.. chapters.Select(id => TestData.RawNode(id, "chapter", parent: parent?.Value))]));
        await server.RememberAsync(Series("good"), Series("bad")).ConfigureAwait(true);
        await server.SendAsync(HttpMethod.Put, "/v1/library?ref=com.example.test:good").ConfigureAwait(true);
        await server.SendAsync(HttpMethod.Put, "/v1/library?ref=com.example.test:bad").ConfigureAwait(true);
        await server.GetAsync("/v1/nodes/children?ref=com.example.test:good").ConfigureAwait(true);
        chapters.Add("c2");

        ApiResponse refreshed = await server.SendAsync(HttpMethod.Post, "/v1/library/refresh").ConfigureAwait(true);
        ApiResponse missing = await server.SendAsync(HttpMethod.Post, "/v1/library/refresh?library=Nowhere").ConfigureAwait(true);
        ApiResponse bob = await server.SendAsync(HttpMethod.Post, "/v1/library/refresh", ApiTestServer.BobKey).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        Assert.Equal(2, refreshed.Json["checked"]!.GetValue<int>());
        Assert.Equal("com.example.test:good", refreshed.Json["updated"]![0]!["node"]!["ref"]!.GetValue<string>());
        Assert.Equal("com.example.test:c2", refreshed.Json["updated"]![0]!["new_children"]![0]!["ref"]!.GetValue<string>());
        Assert.Equal("com.example.test:bad", refreshed.Json["failed"]![0]!["node"]!.GetValue<string>());
        Assert.Equal("unavailable", refreshed.Json["failed"]![0]!["error"]!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.Equal(0, bob.Json["checked"]!.GetValue<int>());
    });

    /// <summary>Refreshing a cover asks the plugin again and returns the node; an unseen node is not found.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task RefreshCoverReturnsTheNode() => ApiTests.WithServerAsync(async server =>
    {
        server.Gateway.OnGet = (_, ids) => Outcome.Success<IReadOnlyList<PluginNode>>(
            [.. ids.Select(id => new PluginNode(id.Value, "series", "Title", null, "https://example.com/new.jpg", []))]);
        await server.RememberAsync(Series("s1")).ConfigureAwait(true);

        ApiResponse refreshed = await server.SendAsync(HttpMethod.Post, "/v1/nodes/refresh_cover?ref=com.example.test:s1").ConfigureAwait(true);
        ApiResponse unseen = await server.SendAsync(HttpMethod.Post, "/v1/nodes/refresh_cover?ref=com.example.test:never").ConfigureAwait(true);
        ApiResponse noRef = await server.SendAsync(HttpMethod.Post, "/v1/nodes/refresh_cover").ConfigureAwait(true);
        ApiResponse unauthenticated = await server.SendAsync(HttpMethod.Post, "/v1/nodes/refresh_cover?ref=com.example.test:s1", key: null).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        Assert.Equal("https://example.com/new.jpg", refreshed.Json["thumbnail"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, unseen.Status);
        Assert.Equal(HttpStatusCode.BadRequest, noRef.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.Status);
    });

    private static Node Series(string id) => new(TestData.Ref(id), NodeKind.Create("series"), $"Title {id}", null, null, []);
}
