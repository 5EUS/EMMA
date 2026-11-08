using System.Net;

using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Api.Tests;

/// <summary>
/// Tests the production composition end to end: a signed fixture plugin is installed, then reached over HTTP
/// through the real loader, the native Wasm host and every pipeline. Nothing is faked.
/// </summary>
public sealed class WasmEndToEndTests
{
    private const string Plugin = "com.example.fixture";

    /// <summary>A search, a drill-down and a progress write all work against a real plugin.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RealPluginServesTheWholeApi()
    {
        ApiTestServer server = await ApiTestServer.StartWithWasmAsync().ConfigureAwait(true);
        await using (server.ConfigureAwait(true))
        {
            Outcome<InstalledPlugin> installed = await server.InstallPackageAsync(await TestPackages.FixtureAsync("fixture", Plugin).ConfigureAwait(true)).ConfigureAwait(true);
            Assert.True(installed.IsSuccess, installed.Error?.Message);

            ApiResponse plugins = await server.GetAsync("/v1/plugins").ConfigureAwait(true);
            Assert.Equal(Plugin, plugins.Json["plugins"]![0]!["id"]!.GetValue<string>());

            ApiResponse detail = await server.GetAsync($"/v1/plugins/{Plugin}").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, detail.Status);
            Assert.NotEmpty(detail.Json["info"]!["kinds"]!.AsArray());

            ApiResponse search = await server.SendAsync(HttpMethod.Post, $"/v1/plugins/{Plugin}/search", json: @"{""text"":""series"",""limit"":2}").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, search.Status);
            Assert.Equal(2, search.Json["items"]!.AsArray().Count);
            Assert.NotNull(search.Json["next_cursor"]);
            string reference = search.Json["items"]![0]!["ref"]!.GetValue<string>();
            Assert.StartsWith($"{Plugin}:", reference, StringComparison.Ordinal);

            ApiResponse children = await server.GetAsync($"/v1/nodes/children?ref={Uri.EscapeDataString(reference)}&limit=5").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, children.Status);
            Assert.Equal(5, children.Json["items"]!.AsArray().Count);

            ApiResponse saved = await server.SendAsync(HttpMethod.Put, $"/v1/library?ref={Uri.EscapeDataString(reference)}").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NoContent, saved.Status);

            ApiResponse progress = await server.SendAsync(HttpMethod.Put, $"/v1/progress?ref={Uri.EscapeDataString(reference)}", json: @"{""unit"":""page"",""value"":2}").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, progress.Status);
        }
    }

    /// <summary>A plugin's own failure reaches the client as a mapped HTTP error, not a crash.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task PluginFailuresAreMappedNotThrown()
    {
        ApiTestServer server = await ApiTestServer.StartWithWasmAsync().ConfigureAwait(true);
        await using (server.ConfigureAwait(true))
        {
            await server.InstallPackageAsync(await TestPackages.FixtureAsync("fixture", Plugin).ConfigureAwait(true)).ConfigureAwait(true);

            ApiResponse unauthorized = await server.SendAsync(HttpMethod.Post, $"/v1/plugins/{Plugin}/search", json: @"{""text"":""error:unauthorized""}").ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.FailedDependency, unauthorized.Status);
            Assert.Equal("plugin_unauthorized", unauthorized.ErrorCode);
        }
    }

    /// <summary>An unsigned plugin cannot be installed under the production trust policy, so it can never be served.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UnsignedPluginsAreRefused()
    {
        ApiTestServer server = await ApiTestServer.StartWithWasmAsync().ConfigureAwait(true);
        await using (server.ConfigureAwait(true))
        {
            Outcome<InstalledPlugin> installed = await server.InstallPackageAsync(await TestPackages.FixtureAsync("fixture", Plugin, sign: false).ConfigureAwait(true)).ConfigureAwait(true);

            ApiResponse plugins = await server.GetAsync("/v1/plugins").ConfigureAwait(true);
            Assert.False(installed.IsSuccess);
            Assert.Empty(plugins.Json["plugins"]!.AsArray());
        }
    }
}
