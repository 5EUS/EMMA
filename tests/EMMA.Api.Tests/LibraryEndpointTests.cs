using System.Net;

using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Api.Tests;

/// <summary>Tests for the caller's saved nodes.</summary>
public sealed class LibraryEndpointTests
{
    private const string Series = "/v1/library?ref=com.example.test:s1";

    /// <summary>A node the host has served can be saved, listed and removed.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task SaveListAndRemove() => ApiTests.WithServerAsync(async server =>
    {
        await server.RememberAsync(new Node(TestData.Ref("s1"), NodeKind.Create("series"), "S", null, null, [])).ConfigureAwait(true);

        ApiResponse add = await server.SendAsync(HttpMethod.Put, Series).ConfigureAwait(true);
        ApiResponse list = await server.GetAsync("/v1/library").ConfigureAwait(true);
        ApiResponse remove = await server.SendAsync(HttpMethod.Delete, Series).ConfigureAwait(true);
        ApiResponse afterRemove = await server.GetAsync("/v1/library").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NoContent, add.Status);
        Assert.Equal("com.example.test:s1", list.Json["entries"]![0]!["node"]!.GetValue<string>());
        Assert.NotNull(list.Json["entries"]![0]!["added_at"]);
        Assert.Equal(HttpStatusCode.NoContent, remove.Status);
        Assert.Empty(afterRemove.Json["entries"]!.AsArray());
    });

    /// <summary>A node the host has never served cannot be saved, so a library never holds something nothing can resolve.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task UnseenNodesCannotBeSaved() => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse response = await server.SendAsync(HttpMethod.Put, Series).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
    });

    /// <summary>Removing something that is not saved is not found.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task RemovingAbsentEntryIsNotFound() => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse response = await server.SendAsync(HttpMethod.Delete, Series).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
    });

    /// <summary>Each client has its own library.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task LibrariesAreSeparate() => ApiTests.WithServerAsync(async server =>
    {
        await server.RememberAsync(new Node(TestData.Ref("s1"), NodeKind.Create("series"), "S", null, null, [])).ConfigureAwait(true);
        await server.SendAsync(HttpMethod.Put, Series).ConfigureAwait(true);

        ApiResponse bob = await server.GetAsync("/v1/library", ApiTestServer.BobKey).ConfigureAwait(true);
        ApiResponse bobRemove = await server.SendAsync(HttpMethod.Delete, Series, ApiTestServer.BobKey).ConfigureAwait(true);
        ApiResponse alice = await server.GetAsync("/v1/library").ConfigureAwait(true);

        Assert.Empty(bob.Json["entries"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, bobRemove.Status);
        Assert.Single(alice.Json["entries"]!.AsArray());
    });
}
