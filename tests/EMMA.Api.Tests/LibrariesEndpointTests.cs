using System.Net;

using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Api.Tests;

/// <summary>Tests for named libraries over HTTP.</summary>
public sealed class LibrariesEndpointTests
{
    /// <summary>The default library is always there; libraries are created, listed and deleted, with each client having its own.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task CreateListAndDelete() => ApiTests.WithServerAsync(async server =>
    {
        ApiResponse initial = await server.GetAsync("/v1/libraries").ConfigureAwait(true);
        ApiResponse create = await server.SendAsync(HttpMethod.Post, "/v1/libraries", json: @"{""name"":""Work""}").ConfigureAwait(true);
        ApiResponse duplicate = await server.SendAsync(HttpMethod.Post, "/v1/libraries", json: @"{""name"":""work""}").ConfigureAwait(true);
        ApiResponse blank = await server.SendAsync(HttpMethod.Post, "/v1/libraries", json: @"{""name"":"" ""}").ConfigureAwait(true);
        ApiResponse bob = await server.GetAsync("/v1/libraries", ApiTestServer.BobKey).ConfigureAwait(true);
        ApiResponse deleteDefault = await server.SendAsync(HttpMethod.Delete, "/v1/libraries?name=default").ConfigureAwait(true);
        ApiResponse delete = await server.SendAsync(HttpMethod.Delete, "/v1/libraries?name=WORK").ConfigureAwait(true);
        ApiResponse deleteAgain = await server.SendAsync(HttpMethod.Delete, "/v1/libraries?name=WORK").ConfigureAwait(true);
        ApiResponse noName = await server.SendAsync(HttpMethod.Delete, "/v1/libraries").ConfigureAwait(true);

        Assert.Equal("Default", initial.Json["libraries"]![0]!["name"]!.GetValue<string>());
        Assert.True(initial.Json["libraries"]![0]!["is_default"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, create.Status);
        Assert.Equal("Work", create.Json["name"]!.GetValue<string>());
        Assert.False(create.Json["is_default"]!.GetValue<bool>());
        Assert.Equal(0, create.Json["count"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.Status);
        Assert.Equal(HttpStatusCode.BadRequest, blank.Status);
        Assert.Single(bob.Json["libraries"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, deleteDefault.Status);
        Assert.Equal(HttpStatusCode.NoContent, delete.Status);
        Assert.Equal(HttpStatusCode.NotFound, deleteAgain.Status);
        Assert.Equal(HttpStatusCode.BadRequest, noName.Status);
    });

    /// <summary>Saving into a named library, listing one library, moving, and searching across libraries.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task SaveMoveListAndSearch() => ApiTests.WithServerAsync(async server =>
    {
        await server.RememberAsync(Series("s1", "One Piece"), Series("s2", "Bleach")).ConfigureAwait(true);
        await server.SendAsync(HttpMethod.Post, "/v1/libraries", json: @"{""name"":""Work""}").ConfigureAwait(true);

        ApiResponse intoWork = await server.SendAsync(HttpMethod.Put, "/v1/library?ref=com.example.test:s1&library=work").ConfigureAwait(true);
        ApiResponse intoMissing = await server.SendAsync(HttpMethod.Put, "/v1/library?ref=com.example.test:s2&library=Nowhere").ConfigureAwait(true);
        await server.SendAsync(HttpMethod.Put, "/v1/library?ref=com.example.test:s2").ConfigureAwait(true);
        ApiResponse all = await server.GetAsync("/v1/library").ConfigureAwait(true);
        ApiResponse work = await server.GetAsync("/v1/library?library=Work").ConfigureAwait(true);
        ApiResponse missing = await server.GetAsync("/v1/library?library=Nowhere").ConfigureAwait(true);
        ApiResponse repeated = await server.GetAsync("/v1/library?library=Work&library=Default").ConfigureAwait(true);
        ApiResponse found = await server.GetAsync("/v1/library/search?text=PIECE").ConfigureAwait(true);
        ApiResponse noText = await server.GetAsync("/v1/library/search").ConfigureAwait(true);
        ApiResponse move = await server.SendAsync(HttpMethod.Post, "/v1/library/move?ref=com.example.test:s2&library=WORK").ConfigureAwait(true);
        ApiResponse moveMissingLibrary = await server.SendAsync(HttpMethod.Post, "/v1/library/move?ref=com.example.test:s2&library=Nowhere").ConfigureAwait(true);
        ApiResponse moveNoLibrary = await server.SendAsync(HttpMethod.Post, "/v1/library/move?ref=com.example.test:s2").ConfigureAwait(true);
        ApiResponse afterMove = await server.GetAsync("/v1/library?library=Work").ConfigureAwait(true);
        ApiResponse libraries = await server.GetAsync("/v1/libraries").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NoContent, intoWork.Status);
        Assert.Equal(HttpStatusCode.NotFound, intoMissing.Status);
        Assert.Equal(2, all.Json["entries"]!.AsArray().Count);
        Assert.Equal("com.example.test:s1", work.Json["entries"]![0]!["node"]!.GetValue<string>());
        Assert.Equal("Work", work.Json["entries"]![0]!["library"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.Equal(HttpStatusCode.BadRequest, repeated.Status);
        Assert.Equal("com.example.test:s1", found.Json["entries"]![0]!["node"]!.GetValue<string>());
        Assert.Equal("Work", found.Json["entries"]![0]!["library"]!.GetValue<string>());
        Assert.Single(found.Json["entries"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, noText.Status);
        Assert.Equal(HttpStatusCode.NoContent, move.Status);
        Assert.Equal(HttpStatusCode.NotFound, moveMissingLibrary.Status);
        Assert.Equal(HttpStatusCode.BadRequest, moveNoLibrary.Status);
        Assert.Equal(2, afterMove.Json["entries"]!.AsArray().Count);
        Assert.Equal([0, 2], libraries.Json["libraries"]!.AsArray().Select(item => item!["count"]!.GetValue<int>()));
    });

    /// <summary>A client cannot move, list or search another client's saved nodes.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task LibrariesArePrivate() => ApiTests.WithServerAsync(async server =>
    {
        await server.RememberAsync(Series("s1", "One Piece")).ConfigureAwait(true);
        await server.SendAsync(HttpMethod.Put, "/v1/library?ref=com.example.test:s1").ConfigureAwait(true);

        ApiResponse search = await server.GetAsync("/v1/library/search?text=piece", ApiTestServer.BobKey).ConfigureAwait(true);
        ApiResponse move = await server.SendAsync(HttpMethod.Post, "/v1/library/move?ref=com.example.test:s1&library=Default", ApiTestServer.BobKey).ConfigureAwait(true);

        Assert.Empty(search.Json["entries"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, move.Status);
    });

    private static Node Series(string id, string title) => new(TestData.Ref(id), NodeKind.Create("series"), title, null, null, []);
}
