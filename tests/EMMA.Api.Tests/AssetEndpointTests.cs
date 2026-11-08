using System.Net;
using System.Text.Json.Nodes;

using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Api.Tests;

/// <summary>Tests for resolving assets and serving their bytes.</summary>
public sealed class AssetEndpointTests
{
    private const string Page = "ref=com.example.test:p1&role=image";

    private static readonly byte[] Bytes = [10, 20, 30, 40, 50, 60, 70, 80];

    /// <summary>Listing shows how an asset is delivered but never where it lives or its private token.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ListingHidesWhereAssetsLive() => ApiTests.WithServerAsync(async server =>
    {
        Uri secretUrl = TestData.Url("api.example.com", "/secret/path.png");
        server.Gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>(
        [
            new Asset("image/png", new InlineDelivery(Bytes), Bytes.Length, [new Prop("emma.quality", PropValue.FromText("high"))]),
            new Asset("image/webp", new RemoteDelivery(secretUrl, [new HttpHeader("Cookie", "session=secret")], null), null, []),
            new Asset("video/mp4", new ProxiedDelivery("private-token-123"), 1_000_000, []),
        ]);

        ApiResponse response = await server.GetAsync($"/v1/assets?{Page}").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        JsonArray assets = response.Json["assets"]!.AsArray();
        Assert.Equal(["inline", "remote", "proxied"], assets.Select(asset => asset!["delivery"]!.GetValue<string>()));
        Assert.Equal([0, 1, 2], assets.Select(asset => asset!["index"]!.GetValue<int>()));
        Assert.Equal("image/png", assets[0]!["mime"]!.GetValue<string>());
        Assert.Equal("high", assets[0]!["props"]![0]!["value"]!.GetValue<string>());
        Assert.DoesNotContain("secret", response.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", response.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", response.Text, StringComparison.Ordinal);
    });

    /// <summary>Inline bytes are served with the plugin's type and a locked-down set of headers.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task InlineContentIsServedSafely() => ApiTests.WithServerAsync(async server =>
    {
        server.Gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>([new Asset("image/png", new InlineDelivery(Bytes), null, [])]);

        ApiResponse response = await server.GetAsync($"/v1/assets/content?{Page}").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal(Bytes, response.Body);
        Assert.Equal("image/png", response.Header("Content-Type"));
        Assert.Equal("nosniff", response.Header("X-Content-Type-Options"));
        Assert.Contains("sandbox", response.Header("Content-Security-Policy"), StringComparison.Ordinal);
        Assert.Equal("private", response.Header("Cache-Control"));
        Assert.Null(response.Header("Accept-Ranges"));
    });

    /// <summary>A type a plugin makes up, to smuggle in a header, is replaced by a safe one.</summary>
    /// <param name="mime">The hostile type.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("image/png\r\nSet-Cookie: x=1")]
    [InlineData("not a type/ at all")]
    public Task HostileMimeTypesAreNeutralised(string mime) => ApiTests.WithServerAsync(async server =>
    {
        server.Gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>([new Asset(mime, new InlineDelivery(Bytes), null, [])]);

        ApiResponse response = await server.GetAsync($"/v1/assets/content?{Page}").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("application/octet-stream", response.Header("Content-Type"));
        Assert.Null(response.Header("Set-Cookie"));
    });

    /// <summary>A remote asset is fetched by the host, under the plugin's policy, and served from its own bytes.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task RemoteContentIsFetchedByTheHost() => ApiTests.WithServerAsync(async server =>
    {
        server.Gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>(
            [new Asset("image/png", new RemoteDelivery(TestData.Url("api.example.com", "/p1.png"), [], null), null, [])]);
        server.Fetcher.OnFetch = _ => Outcome.Success(new AssetPayload("image/png", Bytes, DateTimeOffset.UnixEpoch));

        ApiResponse response = await server.GetAsync($"/v1/assets/content?{Page}").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal(Bytes, response.Body);
        Assert.Equal(1, server.Fetcher.Calls);
    });

    /// <summary>A plugin that points outside its network permissions is a plugin error, and nothing is fetched.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task RemoteAssetOutsideThePolicyIsRefused() => ApiTests.WithServerAsync(async server =>
    {
        server.Gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>(
            [new Asset("image/png", new RemoteDelivery(TestData.Url("evil.example.net", "/p1.png"), [], null), null, [])]);

        ApiResponse response = await server.GetAsync($"/v1/assets/content?{Page}").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadGateway, response.Status);
        Assert.Equal("plugin_error", response.ErrorCode);
        Assert.Equal(0, server.Fetcher.Calls);
    });

    /// <summary>A proxied asset honours a byte range with a 206 and a Content-Range.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ProxiedContentSupportsRanges() => ApiTests.WithServerAsync(async server =>
    {
        List<ByteRange?> asked = [];
        server.Gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>([new Asset("video/mp4", new ProxiedDelivery("tok"), Bytes.Length, [])]);
        server.Gateway.OnRead = (_, _, range) =>
        {
            asked.Add(range);
            int start = (int)(range?.Start ?? 0);
            int length = (int)Math.Min(range?.Length ?? Bytes.Length, Bytes.Length - start);
            return Outcome.Success(new Chunk(Bytes.AsMemory(start, length), Bytes.Length));
        };

        ApiResponse partial = await server.SendAsync(
            HttpMethod.Get,
            $"/v1/assets/content?{Page}",
            configure: request => request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 4)).ConfigureAwait(true);
        ApiResponse open = await server.SendAsync(
            HttpMethod.Get,
            $"/v1/assets/content?{Page}",
            configure: request => request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(6, null)).ConfigureAwait(true);
        ApiResponse whole = await server.GetAsync($"/v1/assets/content?{Page}").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.PartialContent, partial.Status);
        Assert.Equal(Bytes[2..5], partial.Body);
        Assert.Equal("bytes 2-4/8", partial.Header("Content-Range"));
        Assert.Equal("bytes", partial.Header("Accept-Ranges"));
        Assert.Equal(HttpStatusCode.PartialContent, open.Status);
        Assert.Equal(Bytes[6..], open.Body);
        Assert.Equal("bytes 6-7/8", open.Header("Content-Range"));
        Assert.Equal(HttpStatusCode.OK, whole.Status);
        Assert.Equal(Bytes, whole.Body);
        Assert.Equal("bytes", whole.Header("Accept-Ranges"));
        Assert.Equal([new ByteRange(2, 3), new ByteRange(6, null), null], asked);
    });

    /// <summary>A range past the end is not satisfiable.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task RangePastTheEndIsNotSatisfiable() => ApiTests.WithServerAsync(async server =>
    {
        server.Gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>([new Asset("video/mp4", new ProxiedDelivery("tok"), null, [])]);
        server.Gateway.OnRead = (_, _, _) => Outcome.Success(new Chunk(ReadOnlyMemory<byte>.Empty, Bytes.Length));

        ApiResponse response = await server.SendAsync(
            HttpMethod.Get,
            $"/v1/assets/content?{Page}",
            configure: request => request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(100, null)).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.Status);
        Assert.Equal("range_not_satisfiable", response.ErrorCode);
    });

    /// <summary>A range on an asset that cannot be read in parts is ignored, and the whole asset is sent.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task RangeOnAnInlineAssetIsIgnored() => ApiTests.WithServerAsync(async server =>
    {
        server.Gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>([new Asset("image/png", new InlineDelivery(Bytes), null, [])]);

        ApiResponse response = await server.SendAsync(
            HttpMethod.Get,
            $"/v1/assets/content?{Page}",
            configure: request => request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 4)).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal(Bytes, response.Body);
    });

    /// <summary>The asset index selects among alternatives, and one past the end is not found.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task IndexSelectsTheAsset() => ApiTests.WithServerAsync(async server =>
    {
        server.Gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>(
        [
            new Asset("image/png", new InlineDelivery(new byte[] { 1 }), null, []),
            new Asset("image/png", new InlineDelivery(new byte[] { 2 }), null, []),
        ]);

        ApiResponse second = await server.GetAsync($"/v1/assets/content?{Page}&index=1").ConfigureAwait(true);
        ApiResponse missing = await server.GetAsync($"/v1/assets/content?{Page}&index=2").ConfigureAwait(true);

        Assert.Equal([2], second.Body);
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
    });

    /// <summary>Bad parameters are refused before the plugin is asked anything.</summary>
    /// <param name="query">The query string.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("role=image")]
    [InlineData("ref=com.example.test:p1")]
    [InlineData("ref=com.example.test:p1&role=Not%20A%20Role")]
    [InlineData("ref=com.example.test:p1&role=image&index=-1")]
    [InlineData("ref=com.example.test:p1&role=image&index=5000")]
    [InlineData("ref=com.example.test:p1&role=image&index=x")]
    public Task BadAssetRequestsAreRefused(string query) => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse response = await server.GetAsync($"/v1/assets/content?{query}").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal(0, server.Gateway.ResolveCalls);
    });

    /// <summary>A role the node's kind does not declare is refused using what the host already knows.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task UndeclaredRoleIsUnsupported() => ApiTests.WithServerAsync(async server =>
    {
        await server.RememberAsync(new Node(TestData.Ref("p1"), NodeKind.Create("page"), "Page", null, null, [])).ConfigureAwait(true);

        ApiResponse response = await server.GetAsync("/v1/assets?ref=com.example.test:p1&role=audio").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotImplemented, response.Status);
        Assert.Equal("unsupported", response.ErrorCode);
        Assert.Equal(0, server.Gateway.ResolveCalls);
    });
}
