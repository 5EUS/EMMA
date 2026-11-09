using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Api.Tests;

/// <summary>Tests for the download queue and for serving its files before the plugin is asked.</summary>
public sealed class DownloadEndpointTests
{
    private const string Chapter = "ref=com.example.test:c1&role=image";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A queued chapter is downloaded by the background executor, and its pages are then served without asking the plugin.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ADownloadedPageIsServedWithoutThePlugin() => ApiTests.WithServerAsync(async server =>
    {
        await SeedChapterAsync(server).ConfigureAwait(true);

        ApiResponse queued = await server.SendAsync(HttpMethod.Post, $"/v1/downloads?{Chapter}").ConfigureAwait(true);
        string id = queued.Json["id"]!.GetValue<string>();
        JsonNode done = await WaitForAsync(server, id, "completed").ConfigureAwait(true);
        int resolves = server.Gateway.ResolveCalls;
        ApiResponse content = await server.GetAsync("/v1/assets/content?ref=com.example.test:c1p2&role=image").ConfigureAwait(true);
        ApiResponse part = await server.SendAsync(HttpMethod.Get, "/v1/assets/content?ref=com.example.test:c1p2&role=image", configure: request => request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 2)).ConfigureAwait(true);
        ApiResponse beyond = await server.SendAsync(HttpMethod.Get, "/v1/assets/content?ref=com.example.test:c1p2&role=image", configure: request => request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(99, null)).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, queued.Status);
        Assert.Equal("queued", queued.Json["state"]!.GetValue<string>());
        Assert.Equal("com.example.test:c1", done["download"]!["ref"]!.GetValue<string>());
        Assert.Equal(2, done["download"]!["items_completed"]!.GetValue<int>());
        Assert.Equal(["c1p1", "c1p2"], done["items"]!.AsArray().Select(item => item!["ref"]!.GetValue<string>()[(item["ref"]!.GetValue<string>().IndexOf(':', StringComparison.Ordinal) + 1)..]));
        Assert.All(done["items"]!.AsArray(), item => Assert.True(item!["stored"]!.GetValue<bool>()));
        Assert.Equal(HttpStatusCode.OK, content.Status);
        Assert.Equal("c1p2", Encoding.UTF8.GetString(content.Body));
        Assert.Equal("image/png", content.Header("Content-Type"));
        Assert.Equal(HttpStatusCode.PartialContent, part.Status);
        Assert.Equal("1p", Encoding.UTF8.GetString(part.Body));
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, beyond.Status);
        Assert.Equal(resolves, server.Gateway.ResolveCalls);
    });

    /// <summary>Listing filters by state, and a download can be fetched, paused, resumed and deleted by id.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ListPauseResumeAndDelete() => ApiTests.WithServerAsync(async server =>
    {
        await SeedChapterAsync(server).ConfigureAwait(true);

        // The plugin fails at first; later it blocks, so the executor cannot finish the job between two of the calls below.
        bool block = false;
        using ManualResetEventSlim release = new(false);
        server.Gateway.OnResolve = (_, _) =>
        {
            if (Volatile.Read(ref block))
            {
                release.Wait(TimeSpan.FromSeconds(15));
            }

            return Outcome.Failure<IReadOnlyList<Asset>>(ErrorCode.NotFound, "gone");
        };
        ApiResponse queued = await server.SendAsync(HttpMethod.Post, $"/v1/downloads?{Chapter}&hint=720p").ConfigureAwait(true);
        string id = queued.Json["id"]!.GetValue<string>();
        JsonNode failed = await WaitForAsync(server, id, "failed").ConfigureAwait(true);

        ApiResponse all = await server.GetAsync("/v1/downloads").ConfigureAwait(true);
        ApiResponse onlyFailed = await server.GetAsync("/v1/downloads?state=failed").ConfigureAwait(true);
        ApiResponse onlyDone = await server.GetAsync("/v1/downloads?state=completed&limit=5").ConfigureAwait(true);
        ApiResponse badState = await server.GetAsync("/v1/downloads?state=bogus").ConfigureAwait(true);
        ApiResponse pauseFailed = await server.SendAsync(HttpMethod.Post, $"/v1/downloads/{id}/pause").ConfigureAwait(true);
        Volatile.Write(ref block, true);
        ApiResponse retried = await server.SendAsync(HttpMethod.Post, $"/v1/downloads/{id}/retry").ConfigureAwait(true);
        ApiResponse paused = await server.SendAsync(HttpMethod.Post, $"/v1/downloads/{id}/pause").ConfigureAwait(true);
        ApiResponse resumed = await server.SendAsync(HttpMethod.Post, $"/v1/downloads/{id}/resume").ConfigureAwait(true);
        ApiResponse cancelled = await server.SendAsync(HttpMethod.Post, $"/v1/downloads/{id}/cancel").ConfigureAwait(true);
        release.Set();
        ApiResponse deleted = await server.SendAsync(HttpMethod.Delete, $"/v1/downloads/{id}").ConfigureAwait(true);
        ApiResponse gone = await server.GetAsync($"/v1/downloads/{id}").ConfigureAwait(true);
        ApiResponse deletedAgain = await server.SendAsync(HttpMethod.Delete, $"/v1/downloads/{id}").ConfigureAwait(true);

        Assert.Equal("720p", failed["download"]!["hint"]!.GetValue<string>());
        Assert.Contains("gone", failed["download"]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Single(all.Json["items"]!.AsArray());
        Assert.Single(onlyFailed.Json["items"]!.AsArray());
        Assert.Empty(onlyDone.Json["items"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, badState.Status);
        Assert.Equal(HttpStatusCode.BadRequest, pauseFailed.Status);
        Assert.Equal("queued", retried.Json["state"]!.GetValue<string>());
        Assert.Equal("paused", paused.Json["state"]!.GetValue<string>());
        Assert.Equal("queued", resumed.Json["state"]!.GetValue<string>());
        Assert.Equal("canceled", cancelled.Json["state"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, deleted.Status);
        Assert.Equal(HttpStatusCode.NotFound, gone.Status);
        Assert.Equal(HttpStatusCode.NotFound, deletedAgain.Status);
    });

    /// <summary>Requests that are malformed or name nothing the host has served are refused.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task BadRequestsAreRefused() => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse unseen = await server.SendAsync(HttpMethod.Post, $"/v1/downloads?{Chapter}").ConfigureAwait(true);
        ApiResponse noRole = await server.SendAsync(HttpMethod.Post, "/v1/downloads?ref=com.example.test:c1").ConfigureAwait(true);
        ApiResponse badRef = await server.SendAsync(HttpMethod.Post, "/v1/downloads?ref=nonsense&role=image").ConfigureAwait(true);
        ApiResponse badHint = await server.SendAsync(HttpMethod.Post, $"/v1/downloads?{Chapter}&hint=").ConfigureAwait(true);
        ApiResponse badId = await server.SendAsync(HttpMethod.Post, "/v1/downloads/not-a-guid/pause").ConfigureAwait(true);
        ApiResponse unknownId = await server.SendAsync(HttpMethod.Post, $"/v1/downloads/{Guid.NewGuid()}/pause").ConfigureAwait(true);
        ApiResponse unauthenticated = await server.SendAsync(HttpMethod.Get, "/v1/downloads", key: null).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, unseen.Status);
        Assert.Equal(HttpStatusCode.BadRequest, noRole.Status);
        Assert.Equal(HttpStatusCode.BadRequest, badRef.Status);
        Assert.Equal(HttpStatusCode.BadRequest, badHint.Status);
        Assert.Equal(HttpStatusCode.NotFound, badId.Status);
        Assert.Equal(HttpStatusCode.NotFound, unknownId.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.Status);
    });

    /// <summary>Clearing needs a state; retry-failed and the parallel limit work over the whole queue.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ClearRetryFailedAndTheParallelLimit() => ApiTests.WithServerAsync(async server =>
    {
        await SeedChapterAsync(server).ConfigureAwait(true);
        server.Gateway.OnResolve = (_, _) => Outcome.Failure<IReadOnlyList<Asset>>(ErrorCode.NotFound, "gone");
        string id = (await server.SendAsync(HttpMethod.Post, $"/v1/downloads?{Chapter}").ConfigureAwait(true)).Json["id"]!.GetValue<string>();
        await WaitForAsync(server, id, "failed").ConfigureAwait(true);

        ApiResponse noState = await server.SendAsync(HttpMethod.Delete, "/v1/downloads").ConfigureAwait(true);
        ApiResponse settings = await server.GetAsync("/v1/downloads/settings").ConfigureAwait(true);
        ApiResponse set = await server.SendAsync(HttpMethod.Put, "/v1/downloads/settings", json: "{\"parallel\":4}").ConfigureAwait(true);
        ApiResponse tooMany = await server.SendAsync(HttpMethod.Put, "/v1/downloads/settings", json: "{\"parallel\":99}").ConfigureAwait(true);
        ApiResponse missing = await server.SendAsync(HttpMethod.Put, "/v1/downloads/settings", json: "{}").ConfigureAwait(true);
        ApiResponse retryAll = await server.SendAsync(HttpMethod.Post, "/v1/downloads/retry-failed").ConfigureAwait(true);
        await WaitForAsync(server, id, "failed").ConfigureAwait(true);
        ApiResponse cleared = await server.SendAsync(HttpMethod.Delete, "/v1/downloads?state=failed&state=canceled").ConfigureAwait(true);
        ApiResponse after = await server.GetAsync("/v1/downloads").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, noState.Status);
        Assert.Equal(2, settings.Json["parallel"]!.GetValue<int>());
        Assert.Equal(4, set.Json["parallel"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.Status);
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        Assert.Equal(1, retryAll.Json["count"]!.GetValue<int>());
        Assert.Equal(1, cleared.Json["count"]!.GetValue<int>());
        Assert.Empty(after.Json["items"]!.AsArray());
    });

    /// <summary>The hint on an asset request reaches the plugin as the quality preference.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task TheAssetHintReachesThePlugin() => ApiTests.WithServerAsync(async server =>
    {
        IReadOnlyList<Prop>? seen = null;
        server.Gateway.OnResolve = (_, request) =>
        {
            seen = request.Hints;
            return Outcome.Success<IReadOnlyList<Asset>>([new Asset("image/png", new InlineDelivery(new byte[] { 1 }), null, [])]);
        };

        ApiResponse listed = await server.GetAsync("/v1/assets?ref=com.example.test:p1&role=image&hint=1080p").ConfigureAwait(true);
        Prop listedHint = Assert.Single(seen!);
        seen = null;
        ApiResponse content = await server.GetAsync("/v1/assets/content?ref=com.example.test:p1&role=image&hint=480p").ConfigureAwait(true);
        ApiResponse bad = await server.GetAsync("/v1/assets?ref=com.example.test:p1&role=image&hint=").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, listed.Status);
        Assert.Equal(("emma.quality", "1080p"), (listedHint.Key, listedHint.Value.Text));
        Assert.Equal("480p", Assert.Single(seen!).Value.Text);
        Assert.Equal(HttpStatusCode.OK, content.Status);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
    });

    private static async Task SeedChapterAsync(ApiTestServer server)
    {
        await server.RememberAsync(new Node(TestData.Ref("c1"), NodeKind.Create("chapter"), "C1", null, null, [])).ConfigureAwait(false);
        server.Gateway.OnChildren = (_, parent, _) => Outcome.Success(string.Equals(parent?.Value, "c1", StringComparison.Ordinal)
            ? TestData.RawPage(null, TestData.RawNode("c1p1", "page"), TestData.RawNode("c1p2", "page"))
            : TestData.RawPage(null));
        server.Gateway.OnResolve = (_, request) =>
            Outcome.Success<IReadOnlyList<Asset>>([new Asset("image/png", new InlineDelivery(Encoding.UTF8.GetBytes(request.Node.Value)), null, [])]);
    }

    private static async Task<JsonNode> WaitForAsync(ApiTestServer server, string id, string state)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            ApiResponse response = await server.GetAsync($"/v1/downloads/{id}").ConfigureAwait(false);
            JsonNode detail = response.Json;
            if (string.Equals(detail["download"]?["state"]?.GetValue<string>(), state, StringComparison.Ordinal))
            {
                return detail;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), limit.Token).ConfigureAwait(false);
        }
    }
}
