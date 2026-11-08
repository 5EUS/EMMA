using System.Net;

using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Api.Tests;

/// <summary>Tests for the history endpoints: grouping, series detail, deleting, completing and retention.</summary>
public sealed class HistoryEndpointTests
{
    private const string Page = @"{""unit"":""page"",""value"":3,""total"":20}";

    /// <summary>History lists one entry per series, with counts and the latest chapter.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task HistoryGroupsBySeries() => ApiTests.WithServerAsync(async server =>
    {
        await SeedAsync(server).ConfigureAwait(true);

        ApiResponse history = await server.GetAsync("/v1/progress/history").ConfigureAwait(true);
        ApiResponse bob = await server.GetAsync("/v1/progress/history", ApiTestServer.BobKey).ConfigureAwait(true);
        ApiResponse badLimit = await server.GetAsync("/v1/progress/history?limit=0").ConfigureAwait(true);

        System.Text.Json.Nodes.JsonNode group = history.Json["groups"]![0]!;
        Assert.Single(history.Json["groups"]!.AsArray());
        Assert.Equal("com.example.test:s1", group["series"]!.GetValue<string>());
        Assert.Equal("com.example.test:c2", group["latest"]!["node"]!.GetValue<string>());
        Assert.Equal(1, group["completed"]!.GetValue<int>());
        Assert.Equal(1, group["in_progress"]!.GetValue<int>());
        Assert.Empty(bob.Json["groups"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.Status);
    });

    /// <summary>The series view lists every chapter with progress and counts them.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task SeriesDetail() => ApiTests.WithServerAsync(async server =>
    {
        await SeedAsync(server).ConfigureAwait(true);

        ApiResponse series = await server.GetAsync("/v1/progress/series?ref=com.example.test:s1").ConfigureAwait(true);
        ApiResponse nothing = await server.GetAsync("/v1/progress/series?ref=com.example.test:elsewhere").ConfigureAwait(true);
        ApiResponse noRef = await server.GetAsync("/v1/progress/series").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, series.Status);
        Assert.Equal("com.example.test:s1", series.Json["container"]!.GetValue<string>());
        Assert.Equal(1, series.Json["completed"]!.GetValue<int>());
        Assert.Equal(1, series.Json["in_progress"]!.GetValue<int>());
        Assert.Equal(2, series.Json["items"]!.AsArray().Count);
        Assert.NotNull(series.Json["latest_activity"]);
        Assert.Equal(0, nothing.Json["completed"]!.GetValue<int>());
        Assert.Null(nothing.Json["latest_activity"]);
        Assert.Equal(HttpStatusCode.BadRequest, noRef.Status);
    });

    /// <summary>Deleting a series removes its chapters' progress, clearing removes everything, and neither touches another client.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task DeleteAndClear() => ApiTests.WithServerAsync(async server =>
    {
        await SeedAsync(server).ConfigureAwait(true);
        await server.SendAsync(HttpMethod.Put, "/v1/progress?ref=com.example.test:c1", ApiTestServer.BobKey, Page).ConfigureAwait(true);

        ApiResponse noRef = await server.SendAsync(HttpMethod.Delete, "/v1/progress").ConfigureAwait(true);
        ApiResponse deleted = await server.SendAsync(HttpMethod.Delete, "/v1/progress?ref=com.example.test:s1").ConfigureAwait(true);
        ApiResponse again = await server.SendAsync(HttpMethod.Delete, "/v1/progress?ref=com.example.test:s1").ConfigureAwait(true);
        ApiResponse bobStill = await server.GetAsync("/v1/progress/recent", ApiTestServer.BobKey).ConfigureAwait(true);
        ApiResponse cleared = await server.SendAsync(HttpMethod.Post, "/v1/progress/clear", ApiTestServer.BobKey).ConfigureAwait(true);
        ApiResponse bobAfter = await server.GetAsync("/v1/progress/recent", ApiTestServer.BobKey).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, noRef.Status);
        Assert.Equal(2, deleted.Json["removed"]!.GetValue<int>());
        Assert.Equal(0, again.Json["removed"]!.GetValue<int>());
        Assert.Single(bobStill.Json["items"]!.AsArray());
        Assert.Equal(1, cleared.Json["removed"]!.GetValue<int>());
        Assert.Empty(bobAfter.Json["items"]!.AsArray());
    });

    /// <summary>Marking nodes complete finishes them, keeping positions that exist.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task CompleteMarksNodesFinished() => ApiTests.WithServerAsync(async server =>
    {
        await SeedAsync(server).ConfigureAwait(true);

        ApiResponse complete = await server.SendAsync(HttpMethod.Post, "/v1/progress/complete?ref=com.example.test:c2&ref=com.example.test:c3").ConfigureAwait(true);
        ApiResponse noRef = await server.SendAsync(HttpMethod.Post, "/v1/progress/complete").ConfigureAwait(true);
        ApiResponse series = await server.GetAsync("/v1/progress/series?ref=com.example.test:s1").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, complete.Status);
        Assert.Equal(20, complete.Json["items"]![0]!["value"]!.GetValue<double>());
        Assert.Equal("item", complete.Json["items"]![1]!["unit"]!.GetValue<string>());
        Assert.True(complete.Json["items"]![1]!["completed"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.BadRequest, noRef.Status);
        Assert.Equal(2, series.Json["completed"]!.GetValue<int>());
    });

    /// <summary>Retention defaults to forever, is set and read back, and is refused when out of range.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task RetentionRoundTrips() => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse before = await server.GetAsync("/v1/progress/retention").ConfigureAwait(true);
        ApiResponse set = await server.SendAsync(HttpMethod.Put, "/v1/progress/retention", json: @"{""days"":30}").ConfigureAwait(true);
        ApiResponse after = await server.GetAsync("/v1/progress/retention").ConfigureAwait(true);
        ApiResponse bob = await server.GetAsync("/v1/progress/retention", ApiTestServer.BobKey).ConfigureAwait(true);
        ApiResponse tooMany = await server.SendAsync(HttpMethod.Put, "/v1/progress/retention", json: @"{""days"":99999}").ConfigureAwait(true);
        ApiResponse missing = await server.SendAsync(HttpMethod.Put, "/v1/progress/retention", json: "{}").ConfigureAwait(true);

        Assert.Equal(0, before.Json["days"]!.GetValue<int>());
        Assert.Null(before.Json["removed"]);
        Assert.Equal(30, set.Json["days"]!.GetValue<int>());
        Assert.Equal(0, set.Json["removed"]!.GetValue<int>());
        Assert.Equal(30, after.Json["days"]!.GetValue<int>());
        Assert.Equal(0, bob.Json["days"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.Status);
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
    });

    private static async Task SeedAsync(ApiTestServer server)
    {
        NodeKind kind = NodeKind.Create("series");
        await server.RememberAsync(
            new Node(TestData.Ref("s1"), kind, "S", null, null, []),
            new Node(TestData.Ref("c1"), kind, "C1", TestData.Ref("s1"), null, []),
            new Node(TestData.Ref("c2"), kind, "C2", TestData.Ref("s1"), null, [])).ConfigureAwait(false);
        await server.SendAsync(HttpMethod.Put, "/v1/progress?ref=com.example.test:c1", json: @"{""unit"":""page"",""value"":20,""total"":20,""completed"":true}").ConfigureAwait(false);
        await server.SendAsync(HttpMethod.Put, "/v1/progress?ref=com.example.test:c2", json: Page).ConfigureAwait(false);
    }
}
