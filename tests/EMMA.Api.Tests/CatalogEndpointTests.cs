using System.Net;
using System.Text.Json.Nodes;

using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.TestSupport;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace EMMA.Api.Tests;

/// <summary>Tests for the plugin, search, browse and node endpoints.</summary>
public sealed class CatalogEndpointTests
{
    private const string PluginPath = "/v1/plugins/com.example.test";

    /// <summary>Installed plugins are listed with their status.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task InstalledPluginsAreListed() => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse response = await server.GetAsync("/v1/plugins").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        JsonNode plugin = response.Json["plugins"]![0]!;
        Assert.Equal("com.example.test", plugin["id"]!.GetValue<string>());
        Assert.Equal("enabled", plugin["status"]!.GetValue<string>());
        Assert.Equal("1.0.0", plugin["version"]!.GetValue<string>());
    });

    /// <summary>The detail view carries the kinds a generic client navigates by.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task DetailShowsTheDeclaredKinds() => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse response = await server.GetAsync(PluginPath).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        JsonArray kinds = response.Json["info"]!["kinds"]!.AsArray();
        JsonNode series = kinds.Single(kind => string.Equals(kind!["kind"]!.GetValue<string>(), "series", StringComparison.Ordinal))!;
        Assert.Equal("chapter", series["children"]![0]!.GetValue<string>());
        Assert.Equal("image", kinds.Single(kind => string.Equals(kind!["kind"]!.GetValue<string>(), "page", StringComparison.Ordinal))!["roles"]![0]!.GetValue<string>());
    });

    /// <summary>A plugin that is switched off is shown, but not asked anything.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task DisabledPluginHasNoDeclaration() => ApiTests.WithServerAsync(async server =>
    {
        IPluginRegistryPort registry = server.App.Services.GetRequiredService<IPluginRegistryPort>();
        await registry.SetStatusAsync(TestData.Plugin, PluginStatus.Disabled, DateTimeOffset.UnixEpoch, CancellationToken.None).ConfigureAwait(true);

        ApiResponse response = await server.GetAsync(PluginPath).ConfigureAwait(true);

        Assert.Equal("disabled", response.Json["status"]!.GetValue<string>());
        Assert.Null(response.Json["info"]);
        Assert.Equal(0, server.Gateway.DescribeCalls);
    });

    /// <summary>Unknown and malformed plugin ids are told apart.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task UnknownAndMalformedPluginIds() => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse unknown = await server.GetAsync("/v1/plugins/com.example.missing").ConfigureAwait(true);
        ApiResponse malformed = await server.GetAsync("/v1/plugins/NOT%20AN%20ID").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal("not_found", unknown.ErrorCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.Status);
        Assert.Equal("invalid_argument", malformed.ErrorCode);
    });

    /// <summary>A search request reaches the plugin as a typed query, and results come back namespaced.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task SearchRoundTrip() => ApiTests.WithServerAsync(async server =>
    {
        Query? seen = null;
        server.Gateway.OnSearch = (_, query) =>
        {
            seen = query;
            return Outcome.Success(TestData.RawPage("next-1", TestData.RawNode("s1", title: "Berserk"), TestData.RawNode("s2")));
        };

        ApiResponse response = await server.SendAsync(
            HttpMethod.Post,
            $"{PluginPath}/search",
            json: @"{""text"":""berserk"",""kinds"":[""series""],""sort"":null,""limit"":10}").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("com.example.test:s1", response.Json["items"]![0]!["ref"]!.GetValue<string>());
        Assert.Equal("Berserk", response.Json["items"]![0]!["title"]!.GetValue<string>());
        Assert.Equal("next-1", response.Json["next_cursor"]!.GetValue<string>());
        Assert.Equal("berserk", seen?.Text);
        Assert.Equal("series", Assert.Single(seen!.Kinds).Value);
        Assert.Equal(10, seen.Limit);
    });

    /// <summary>Filters are passed through and checked against what the plugin declared.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task SearchFiltersAreValidatedAgainstTheDeclaration() => ApiTests.WithServerAsync(async server =>
    {
        server.Gateway.OnDescribe = _ => Outcome.Success(TestData.Info([TestData.Filter("genre", FilterType.MultiChoice, false, "action", "drama")]));
        Query? seen = null;
        server.Gateway.OnSearch = (_, query) =>
        {
            seen = query;
            return Outcome.Success(TestData.RawPage(null));
        };

        ApiResponse good = await server.SendAsync(HttpMethod.Post, $"{PluginPath}/search", json: @"{""filters"":[{""id"":""genre"",""values"":[""action""]}]}").ConfigureAwait(true);
        ApiResponse bad = await server.SendAsync(HttpMethod.Post, $"{PluginPath}/search", json: @"{""filters"":[{""id"":""nope"",""values"":[""x""]}]}").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, good.Status);
        Assert.Equal(["action"], seen?.Filters.Single().Values);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Equal(1, server.Gateway.SearchCalls);
    });

    /// <summary>A body with a field the API does not know, or that is not JSON, is refused before any plugin is called.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="contentType">The content type to send.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData(@"{""text"":""x"",""bogus"":1}", "application/json")]
    [InlineData(@"{""text"":", "application/json")]
    [InlineData("null", "application/json")]
    [InlineData(@"{""text"":""x""}", "text/plain")]
    [InlineData(@"{""kinds"":[""NOT A KIND""]}", "application/json")]
    [InlineData(@"{""filters"":[{""values"":[""x""]}]}", "application/json")]
    public Task MalformedSearchBodiesAreRefused(string body, string contentType) => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse response = await server.SendAsync(
            HttpMethod.Post,
            $"{PluginPath}/search",
            json: body,
            configure: request => request.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType)).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal("invalid_argument", response.ErrorCode);
        Assert.Equal(0, server.Gateway.SearchCalls);
    });

    /// <summary>A body past the size limit is refused rather than read.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task OversizedBodyIsRefused() => ApiTests.WithServerAsync(async server =>
    {
        string body = $@"{{""text"":""{new string('a', 100 * 1024)}""}}";

        ApiResponse response = await server.SendAsync(HttpMethod.Post, $"{PluginPath}/search", json: body).ConfigureAwait(true);

        Assert.True(response.Status is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal(0, server.Gateway.SearchCalls);
    });

    /// <summary>Browsing and listing children pass the node, cursor and limit to the plugin.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task BrowseAndChildren() => ApiTests.WithServerAsync(async server =>
    {
        LocalNodeId? parent = LocalNodeId.Create("unset");
        PageRequest? page = null;
        server.Gateway.OnChildren = (_, id, request) =>
        {
            parent = id;
            page = request;
            return Outcome.Success(TestData.RawPage("more", TestData.RawNode("c1", kind: "chapter", parent: id?.Value)));
        };

        ApiResponse children = await server.GetAsync("/v1/nodes/children?ref=com.example.test:s1&cursor=abc&limit=2").ConfigureAwait(true);
        Assert.Equal("s1", parent?.Value);
        Assert.Equal(new PageRequest("abc", 2), page);
        Assert.Equal("com.example.test:s1", children.Json["items"]![0]!["parent"]!.GetValue<string>());

        ApiResponse browse = await server.GetAsync($"{PluginPath}/browse").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, browse.Status);
        Assert.Null(parent);
    });

    /// <summary>A node id with slashes and colons survives, because ids travel in a query parameter.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task AwkwardNodeIdsSurvive() => ApiTests.WithServerAsync(async server =>
    {
        string? seen = null;
        server.Gateway.OnChildren = (_, id, _) =>
        {
            seen = id?.Value;
            return Outcome.Success(TestData.RawPage(null));
        };

        ApiResponse response = await server.GetAsync($"/v1/nodes/children?ref={Uri.EscapeDataString("com.example.test:a/b:c d?")}").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("a/b:c d?", seen);
    });

    /// <summary>Paging parameters outside their range are refused.</summary>
    /// <param name="query">The query string.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=501")]
    [InlineData("limit=ten")]
    [InlineData("limit=-1")]
    [InlineData("limit=1&limit=2")]
    public Task BadPagingIsRefused(string query) => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse response = await server.GetAsync($"/v1/nodes/children?ref=com.example.test:s1&{query}").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal(0, server.Gateway.ChildrenCalls);
    });

    /// <summary>A missing or malformed reference is refused.</summary>
    /// <param name="path">The request path.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("/v1/nodes/children")]
    [InlineData("/v1/nodes/children?ref=no-colon")]
    [InlineData("/v1/nodes/children?ref=a:b&ref=c:d")]
    [InlineData("/v1/nodes")]
    [InlineData("/v1/nodes?ref=Bad%20Plugin:x")]
    public Task BadReferencesAreRefused(string path) => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse response = await server.GetAsync(path).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal("invalid_argument", response.ErrorCode);
    });

    /// <summary>Nodes can be fetched by reference, in the order asked, and unknown ones are omitted.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task NodesAreFetchedInRequestedOrder() => ApiTests.WithServerAsync(async server =>
    {
        server.Gateway.OnGet = (_, ids) => Outcome.Success<IReadOnlyList<PluginNode>>([.. ids.Where(id => !string.Equals(id.Value, "gone", StringComparison.Ordinal)).Select(id => TestData.RawNode(id.Value))]);

        ApiResponse response = await server.GetAsync("/v1/nodes?ref=com.example.test:b&ref=com.example.test:gone&ref=com.example.test:a").ConfigureAwait(true);

        Assert.Equal(["com.example.test:b", "com.example.test:a"], response.Json["nodes"]!.AsArray().Select(node => node!["ref"]!.GetValue<string>()));
    });

    /// <summary>Every property type has a stable JSON shape.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task PropsKeepTheirTypes() => ApiTests.WithServerAsync(async server =>
    {
        string thumbnail = TestData.Url("img.example.com", "/t.png").AbsoluteUri;
        PluginNode node = new(
            "s1",
            "series",
            "T",
            null,
            thumbnail,
            [
                new Prop("emma.synopsis", PropValue.FromText("hello")),
                new Prop("emma.chapters", PropValue.FromWholeNumber(12)),
                new Prop("emma.rating", PropValue.FromNumber(4.5)),
                new Prop("emma.adult", PropValue.FromFlag(true)),
                new Prop("emma.published", PropValue.FromTimestamp(1700000000000)),
                new Prop("emma.site", PropValue.FromUrl(TestData.Url("example.com", "/a"))),
                new Prop("emma.tags", PropValue.FromTextList(["a", "b"])),
            ]);
        server.Gateway.OnSearch = (_, _) => Outcome.Success(new PluginNodePage([node], null, 1, []));

        ApiResponse response = await server.SendAsync(HttpMethod.Post, $"{PluginPath}/search", json: "{}").ConfigureAwait(true);

        JsonNode item = response.Json["items"]![0]!;
        Assert.Equal(thumbnail, item["thumbnail"]!.GetValue<string>());
        Assert.Equal(1, response.Json["total"]!.GetValue<long>());
        Dictionary<string, JsonNode> props = item["props"]!.AsArray().ToDictionary(prop => prop!["key"]!.GetValue<string>(), prop => prop!, StringComparer.Ordinal);
        Assert.Equal("text", props["emma.synopsis"]["type"]!.GetValue<string>());
        Assert.Equal("hello", props["emma.synopsis"]["value"]!.GetValue<string>());
        Assert.Equal("whole_number", props["emma.chapters"]["type"]!.GetValue<string>());
        Assert.Equal(12, props["emma.chapters"]["value"]!.GetValue<long>());
        Assert.Equal(4.5, props["emma.rating"]["value"]!.GetValue<double>());
        Assert.True(props["emma.adult"]["value"]!.GetValue<bool>());
        Assert.Equal("timestamp", props["emma.published"]["type"]!.GetValue<string>());
        Assert.Equal(1700000000000, props["emma.published"]["value"]!.GetValue<long>());
        Assert.Equal("url", props["emma.site"]["type"]!.GetValue<string>());
        Assert.Equal(["a", "b"], props["emma.tags"]["value"]!.AsArray().Select(tag => tag!.GetValue<string>()));
    });

    /// <summary>Each failure class maps to a distinct, stable status and code, and a retry hint becomes a header.</summary>
    /// <param name="code">The plugin failure class.</param>
    /// <param name="status">The expected HTTP status.</param>
    /// <param name="apiCode">The expected error code.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData(ErrorCode.NotFound, HttpStatusCode.NotFound, "not_found")]
    [InlineData(ErrorCode.InvalidArgument, HttpStatusCode.BadRequest, "invalid_argument")]
    [InlineData(ErrorCode.Unsupported, HttpStatusCode.NotImplemented, "unsupported")]
    [InlineData(ErrorCode.Unauthorized, HttpStatusCode.FailedDependency, "plugin_unauthorized")]
    [InlineData(ErrorCode.RateLimited, HttpStatusCode.TooManyRequests, "upstream_rate_limited")]
    [InlineData(ErrorCode.Unavailable, HttpStatusCode.ServiceUnavailable, "unavailable")]
    [InlineData(ErrorCode.Cancelled, HttpStatusCode.GatewayTimeout, "timeout")]
    [InlineData(ErrorCode.Internal, HttpStatusCode.BadGateway, "plugin_error")]
    public Task PluginFailuresMapToStatuses(ErrorCode code, HttpStatusCode status, string apiCode) => ApiTests.WithServerAsync(async server =>
    {
        server.Gateway.OnSearch = (_, _) => Outcome.Failure<PluginNodePage>(new PluginError(code, "upstream said no", TimeSpan.FromSeconds(7)));

        ApiResponse response = await server.SendAsync(HttpMethod.Post, $"{PluginPath}/search", json: "{}").ConfigureAwait(true);

        Assert.Equal(status, response.Status);
        Assert.Equal(apiCode, response.ErrorCode);
        Assert.Equal("upstream said no", response.Json["error"]!["message"]!.GetValue<string>());
        Assert.Equal("7", response.Header("Retry-After"));
        Assert.Equal(7000, response.Json["error"]!["retry_after_ms"]!.GetValue<long>());
    });

    /// <summary>A bug in the host becomes a generic JSON error that leaks nothing.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task UnexpectedFailuresDoNotLeakDetails() => ApiTests.WithServerAsync(async server =>
    {
        server.Gateway.OnSearch = (_, _) => throw new InvalidOperationException("secret connection string");

        ApiResponse response = await server.SendAsync(HttpMethod.Post, $"{PluginPath}/search", json: "{}").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.InternalServerError, response.Status);
        Assert.Equal("internal_error", response.ErrorCode);
        Assert.DoesNotContain("secret", response.Text, StringComparison.Ordinal);
    });
}
