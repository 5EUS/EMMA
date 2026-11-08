using System.Text;
using System.Text.Json.Nodes;

using Xunit;

namespace EMMA.Native.Tests;

/// <summary>Tests the operations against a real plugin in the real Wasm host, installed through the library itself.</summary>
public sealed class NativeOperationTests
{
    private const string Series = "com.example.fixture:s1";

    /// <summary>A signed package installs through the library, shows up in the lists and can be switched off and removed.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task PluginLifecycle()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            CallResult installed = await host.InstallFixtureAsync().ConfigureAwait(true);
            Assert.Equal(NativeHarness.Plugin, installed.Ok["id"]!.GetValue<string>());

            CallResult list = host.Call("plugins.list");
            CallResult detail = host.Call("plugins.get", new JsonObject { ["plugin"] = NativeHarness.Plugin });
            Assert.Equal(NativeHarness.Plugin, list.Ok["plugins"]![0]!["id"]!.GetValue<string>());
            Assert.NotEmpty(detail.Ok["info"]!["kinds"]!.AsArray());

            host.Call("plugins.set_status", new JsonObject { ["plugin"] = NativeHarness.Plugin, ["status"] = "disabled" });
            CallResult off = host.Call("catalog.search", Search("series"));
            host.Call("plugins.set_status", new JsonObject { ["plugin"] = NativeHarness.Plugin, ["status"] = "enabled" });
            CallResult on = host.Call("catalog.search", Search("series"));

            Assert.Equal("unsupported", off.ErrorCode);
            Assert.NotNull(on.Ok["items"]);

            CallResult removed = host.Call("plugins.uninstall", new JsonObject { ["plugin"] = NativeHarness.Plugin });
            CallResult again = host.Call("plugins.uninstall", new JsonObject { ["plugin"] = NativeHarness.Plugin });
            Assert.Equal(NativeStatus.Ok, removed.Status);
            Assert.NotNull(removed.Ok);
            Assert.Equal("not_found", again.ErrorCode);
            Assert.Empty(host.Call("plugins.list").Ok["plugins"]!.AsArray());
        }
    }

    /// <summary>Installing garbage, an unsigned package or a missing file fails with a reason and installs nothing.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task BadInstallsAreRefused()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            string garbage = Path.Combine(Path.GetTempPath(), $"emma-native-garbage-{Guid.NewGuid():N}.zip");
            await File.WriteAllBytesAsync(garbage, Encoding.UTF8.GetBytes("not a zip"), TestContext.Current.CancellationToken).ConfigureAwait(true);
            try
            {
                CallResult notZip = host.Call("plugins.install", new JsonObject { ["package_path"] = garbage, ["repository"] = "test-repo" });
                CallResult missing = host.Call("plugins.install", new JsonObject { ["package_path"] = garbage + ".nope", ["repository"] = "test-repo" });
                CallResult noRepository = host.Call("plugins.install", new JsonObject { ["package_path"] = garbage });

                Assert.Equal("invalid_argument", notZip.ErrorCode);
                Assert.Equal("invalid_argument", missing.ErrorCode);
                Assert.Equal("invalid_argument", noRepository.ErrorCode);
                Assert.Empty(host.Call("plugins.list").Ok["plugins"]!.AsArray());
            }
            finally
            {
                File.Delete(garbage);
            }
        }
    }

    /// <summary>Search pages by cursor, children drill down, and nodes can be fetched by reference, all with the shapes the HTTP API uses.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CatalogOperations()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.InstallFixtureAsync().ConfigureAwait(true);

            CallResult first = host.Call("catalog.search", Search("series", limit: 2));
            string cursor = first.Ok["next_cursor"]!.GetValue<string>();
            CallResult second = host.Call("catalog.search", Search("series", limit: 2, cursor: cursor));
            CallResult children = host.Call("catalog.children", new JsonObject { ["ref"] = Series, ["limit"] = 5 });
            CallResult browse = host.Call("catalog.browse", new JsonObject { ["plugin"] = NativeHarness.Plugin });
            CallResult got = host.Call("catalog.get", new JsonObject { ["refs"] = new JsonArray(Series, "com.example.fixture:nope") });

            Assert.Equal(2, first.Ok["items"]!.AsArray().Count);
            Assert.NotEqual(first.Ok["items"]![0]!["ref"]!.GetValue<string>(), second.Ok["items"]![0]!["ref"]!.GetValue<string>());
            Assert.Equal(5, children.Ok["items"]!.AsArray().Count);
            Assert.StartsWith("com.example.fixture:", children.Ok["items"]![0]!["ref"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal(Series, got.Ok["nodes"]![0]!["ref"]!.GetValue<string>());
            Assert.Single(got.Ok["nodes"]!.AsArray());
            Assert.Equal(NativeStatus.Ok, browse.Status);
        }
    }

    /// <summary>Plugin failures keep their class and message, and invalid requests are refused before the plugin is asked.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task FailuresKeepTheirCode()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.InstallFixtureAsync().ConfigureAwait(true);

            CallResult unauthorized = host.Call("catalog.search", Search("error:unauthorized"));
            CallResult missing = host.Call("catalog.search", new JsonObject { ["plugin"] = "com.example.absent", ["query"] = new JsonObject { ["text"] = "x" } });
            CallResult badPlugin = host.Call("catalog.search", new JsonObject { ["plugin"] = "NOT A PLUGIN" });
            CallResult badLimit = host.Call("catalog.children", new JsonObject { ["ref"] = Series, ["limit"] = 0 });

            Assert.Equal("plugin_unauthorized", unauthorized.ErrorCode);
            Assert.Equal("not_found", missing.ErrorCode);
            Assert.Equal("invalid_argument", badPlugin.ErrorCode);
            Assert.Equal("invalid_argument", badLimit.ErrorCode);
        }
    }

    /// <summary>Assets are listed without their location, read whole or in ranges, and the bytes arrive beside the JSON.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AssetOperations()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.InstallFixtureAsync().ConfigureAwait(true);
            JsonObject inline = new() { ["ref"] = "com.example.fixture:inline1", ["role"] = "text" };
            JsonObject proxied = new() { ["ref"] = "com.example.fixture:proxied1", ["role"] = "image" };

            CallResult listing = host.Call("assets.list", proxied);
            CallResult text = host.Call("assets.content", inline);
            CallResult whole = host.Call("assets.content", proxied);
            CallResult part = host.Call("assets.content", new JsonObject
            {
                ["ref"] = "com.example.fixture:proxied1",
                ["role"] = "image",
                ["range"] = new JsonObject { ["start"] = 10, ["length"] = 5 },
            });
            CallResult beyond = host.Call("assets.content", new JsonObject
            {
                ["ref"] = "com.example.fixture:proxied1",
                ["role"] = "image",
                ["range"] = new JsonObject { ["start"] = 5000 },
            });
            CallResult badIndex = host.Call("assets.content", new JsonObject { ["ref"] = "com.example.fixture:inline1", ["role"] = "text", ["index"] = 3 });

            Assert.Equal("proxied", listing.Ok["assets"]![0]!["delivery"]!.GetValue<string>());
            Assert.DoesNotContain("tok-1", listing.Json.ToJsonString(), StringComparison.Ordinal);
            Assert.Equal("hello", Encoding.UTF8.GetString(text.Blob!));
            Assert.Equal("text/plain", text.Ok["mime"]!.GetValue<string>());
            Assert.Equal(1000, whole.Blob!.Length);
            Assert.Equal(1000, whole.Ok["length"]!.GetValue<long>());
            Assert.Equal(whole.Blob[10..15], part.Blob);
            Assert.Equal(10, part.Ok["range_start"]!.GetValue<long>());
            Assert.Equal(1000, part.Ok["total"]!.GetValue<long>());
            Assert.Equal("range_not_satisfiable", beyond.ErrorCode);
            Assert.Equal("not_found", badIndex.ErrorCode);
        }
    }

    /// <summary>Progress and library belong to the handle's user, and library entries must refer to nodes the host has served.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ProgressAndLibrary()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.InstallFixtureAsync().ConfigureAwait(true);
            host.Call("catalog.search", Search("series"));
            string chapter = host.Call("catalog.children", new JsonObject { ["ref"] = Series, ["limit"] = 1 }).Ok["items"]![0]!["ref"]!.GetValue<string>();

            CallResult set = host.Call("progress.set", new JsonObject { ["ref"] = chapter, ["unit"] = "page", ["value"] = 4, ["total"] = 30 });
            CallResult get = host.Call("progress.get", new JsonObject { ["ref"] = chapter });
            CallResult resume = host.Call("progress.continue", new JsonObject { ["ref"] = Series });
            CallResult recent = host.Call("progress.recent", new JsonObject { ["limit"] = 10 });
            CallResult invalid = host.Call("progress.set", new JsonObject { ["ref"] = chapter, ["unit"] = "Page", ["value"] = 1 });
            CallResult none = host.Call("progress.get", new JsonObject { ["ref"] = Series });

            Assert.Equal(4, set.Ok["value"]!.GetValue<double>());
            Assert.Equal(30, get.Ok["total"]!.GetValue<double>());
            Assert.Equal(chapter, resume.Ok["node"]!.GetValue<string>());
            Assert.Equal(chapter, recent.Ok["items"]![0]!["node"]!.GetValue<string>());
            Assert.Equal("invalid_argument", invalid.ErrorCode);
            Assert.Equal("not_found", none.ErrorCode);

            CallResult unseen = host.Call("library.add", new JsonObject { ["ref"] = "com.example.fixture:never-seen" });
            CallResult added = host.Call("library.add", new JsonObject { ["ref"] = Series });
            CallResult library = host.Call("library.list");
            CallResult removed = host.Call("library.remove", new JsonObject { ["ref"] = Series });
            CallResult removedAgain = host.Call("library.remove", new JsonObject { ["ref"] = Series });

            Assert.Equal("not_found", unseen.ErrorCode);
            Assert.NotNull(added.Ok);
            Assert.Equal(Series, library.Ok["entries"]![0]!["node"]!.GetValue<string>());
            Assert.NotNull(removed.Ok);
            Assert.Equal("not_found", removedAgain.ErrorCode);
        }
    }

    private static JsonObject Search(string text, int? limit = null, string? cursor = null)
    {
        JsonObject query = new() { ["text"] = text };
        if (limit is { } size)
        {
            query["limit"] = size;
        }

        if (cursor is not null)
        {
            query["cursor"] = cursor;
        }

        return new JsonObject { ["plugin"] = NativeHarness.Plugin, ["query"] = query };
    }
}
