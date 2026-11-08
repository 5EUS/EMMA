using System.Net;

using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Api.Tests;

/// <summary>Tests for reading and changing a plugin's settings over HTTP.</summary>
public sealed class SettingsEndpointTests
{
    private const string Path = "/v1/plugins/com.example.test/settings";

    /// <summary>Every declared setting is listed with where it stands, and a required one nobody has set is flagged.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ListsDeclaredSettings() => WithDeclaredSettingsAsync(async server =>
    {
        ApiResponse response = await server.GetAsync(Path).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var settings = response.Json["settings"]!.AsArray();
        Assert.Equal(["user", "token", "quality"], settings.Select(setting => setting!["key"]!.GetValue<string>()));
        Assert.False(settings[0]!["is_set"]!.GetValue<bool>());
        Assert.True(settings[1]!["missing"]!.GetValue<bool>());
        Assert.False(settings[2]!["missing"]!.GetValue<bool>());
    });

    /// <summary>A change is stored and answered with the new state; a secret is reported as set and never echoed.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task SetStoresAndNeverEchoesSecrets() => WithDeclaredSettingsAsync(async server =>
    {
        ApiResponse put = await server.SendAsync(HttpMethod.Put, Path, json: @"{""changes"":[{""key"":""user"",""values"":[""me""]},{""key"":""token"",""values"":[""hunter2""]}]}").ConfigureAwait(true);
        ApiResponse get = await server.GetAsync(Path).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.DoesNotContain("hunter2", put.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", get.Text, StringComparison.Ordinal);
        var settings = get.Json["settings"]!.AsArray();
        Assert.Equal("me", settings[0]!["values"]![0]!.GetValue<string>());
        Assert.True(settings[1]!["is_set"]!.GetValue<bool>());
        Assert.Empty(settings[1]!["values"]!.AsArray());
        Assert.False(settings[1]!["missing"]!.GetValue<bool>());
    });

    /// <summary>An empty list of values clears the setting.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task EmptyValuesClear() => WithDeclaredSettingsAsync(async server =>
    {
        await server.SendAsync(HttpMethod.Put, Path, json: @"{""changes"":[{""key"":""user"",""values"":[""me""]}]}").ConfigureAwait(true);

        ApiResponse cleared = await server.SendAsync(HttpMethod.Put, Path, json: @"{""changes"":[{""key"":""user"",""values"":[]}]}").ConfigureAwait(true);

        Assert.False(cleared.Json["settings"]![0]!["is_set"]!.GetValue<bool>());
    });

    /// <summary>A value that does not suit the setting is a 400 naming the setting, and nothing in the request is stored.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task InvalidValueIsRefusedWholesale() => WithDeclaredSettingsAsync(async server =>
    {
        ApiResponse put = await server.SendAsync(HttpMethod.Put, Path, json: @"{""changes"":[{""key"":""user"",""values"":[""me""]},{""key"":""quality"",""values"":[""ultra""]}]}").ConfigureAwait(true);
        ApiResponse get = await server.GetAsync(Path).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, put.Status);
        Assert.Equal("invalid_argument", put.ErrorCode);
        Assert.Contains("quality", put.Text, StringComparison.Ordinal);
        Assert.False(get.Json["settings"]![0]!["is_set"]!.GetValue<bool>());
    });

    /// <summary>Malformed requests are refused: a change with no key or no values, no changes at all, or an unknown field.</summary>
    /// <param name="body">The request body.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData(@"{}")]
    [InlineData(@"{""changes"":[{""values"":[""x""]}]}")]
    [InlineData(@"{""changes"":[{""key"":""user""}]}")]
    [InlineData(@"{""changes"":[],""extra"":1}")]
    [InlineData(@"not json")]
    public Task MalformedRequestsAreRefused(string body) => WithDeclaredSettingsAsync(async server =>
    {
        ApiResponse put = await server.SendAsync(HttpMethod.Put, Path, json: body).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, put.Status);
    });

    /// <summary>A plugin that cannot describe itself has no settings to show.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task PluginThatCannotRunHasNoSettings() => ApiTests.WithServerAsync(async server =>
    {
        server.Gateway.OnDescribe = _ => Outcome.Failure<PluginInfo>(ErrorCode.Unsupported, "disabled");

        ApiResponse response = await server.GetAsync(Path).ConfigureAwait(true);

        Assert.NotEqual(HttpStatusCode.OK, response.Status);
        Assert.Equal("unsupported", response.ErrorCode);
    });

    /// <summary>Settings are behind authentication like everything else.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task RequireAnApiKey() => WithDeclaredSettingsAsync(async server =>
    {
        ApiResponse read = await server.GetAsync(Path, key: null).ConfigureAwait(true);
        ApiResponse write = await server.SendAsync(HttpMethod.Put, Path, key: null, json: @"{""changes"":[]}").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Unauthorized, read.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, write.Status);
    });

    private static Task WithDeclaredSettingsAsync(Func<ApiTestServer, Task> test) => ApiTests.WithServerAsync(async server =>
    {
        server.Gateway.OnDescribe = _ => Outcome.Success(TestData.Info() with
        {
            Settings =
            [
                new SettingSpec("user", "User", null, null, SettingType.Text, null, false, [], [], null, 20, null),
                new SettingSpec("token", "Token", null, null, SettingType.Secret, null, true, [], [], null, null, null),
                new SettingSpec("quality", "Quality", null, null, SettingType.Choice, null, false, ["high"], [new Choice("high", "High", null, true), new Choice("low", "Low", null, false)], null, null, null),
            ],
        });
        await test(server).ConfigureAwait(true);
    });
}
