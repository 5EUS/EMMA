using System.Text.Json.Nodes;

using Xunit;

namespace EMMA.Native.Tests;

/// <summary>Tests the user-data operations (libraries, history, remembered choices) through the library, against the real fixture plugin.</summary>
public sealed class NativeUserDataTests
{
    private const string Series = "com.example.fixture:s1";

    /// <summary>Named libraries: create, save into one, list, search, move and delete, all as the handle's user.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task NamedLibraries()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.InstallFixtureAsync().ConfigureAwait(true);
            JsonArray found = host.Call("catalog.search", Query("series")).Ok["items"]!.AsArray();
            string title = found.First(item => string.Equals(item!["ref"]!.GetValue<string>(), Series, StringComparison.Ordinal))!["title"]!.GetValue<string>();

            CallResult initial = host.Call("library.libraries");
            CallResult created = host.Call("library.create", new JsonObject { ["name"] = "Work" });
            CallResult duplicate = host.Call("library.create", new JsonObject { ["name"] = "WORK" });
            CallResult added = host.Call("library.add", new JsonObject { ["ref"] = Series, ["library"] = "work" });
            CallResult missingLibrary = host.Call("library.add", new JsonObject { ["ref"] = Series, ["library"] = "Nowhere" });
            CallResult inWork = host.Call("library.list", new JsonObject { ["library"] = "Work" });
            CallResult everywhere = host.Call("library.list");
            CallResult searched = host.Call("library.search", new JsonObject { ["text"] = title.ToUpperInvariant() });
            CallResult noText = host.Call("library.search", new JsonObject { ["limit"] = 5 });
            CallResult moved = host.Call("library.move", new JsonObject { ["ref"] = Series, ["library"] = "Default" });
            CallResult moveNoLibrary = host.Call("library.move", new JsonObject { ["ref"] = Series });
            CallResult inDefault = host.Call("library.list", new JsonObject { ["library"] = "default" });
            CallResult deleteDefault = host.Call("library.delete", new JsonObject { ["name"] = "Default" });
            CallResult deleted = host.Call("library.delete", new JsonObject { ["name"] = "Work" });
            CallResult deletedAgain = host.Call("library.delete", new JsonObject { ["name"] = "Work" });
            CallResult unknownField = host.Call("library.list", new JsonObject { ["libary"] = "Work" });

            Assert.True(initial.Ok["libraries"]![0]!["is_default"]!.GetValue<bool>());
            Assert.Equal("Work", created.Ok["name"]!.GetValue<string>());
            Assert.Equal("invalid_argument", duplicate.ErrorCode);
            Assert.NotNull(added.Ok);
            Assert.Equal("not_found", missingLibrary.ErrorCode);
            Assert.Equal("Work", inWork.Ok["entries"]![0]!["library"]!.GetValue<string>());
            Assert.Single(everywhere.Ok["entries"]!.AsArray());
            Assert.Equal(Series, searched.Ok["entries"]![0]!["node"]!.GetValue<string>());
            Assert.Equal("Work", searched.Ok["entries"]![0]!["library"]!.GetValue<string>());
            Assert.Equal("invalid_argument", noText.ErrorCode);
            Assert.NotNull(moved.Ok);
            Assert.Equal("invalid_argument", moveNoLibrary.ErrorCode);
            Assert.Equal("Default", inDefault.Ok["entries"]![0]!["library"]!.GetValue<string>());
            Assert.Equal("invalid_argument", deleteDefault.ErrorCode);
            Assert.NotNull(deleted.Ok);
            Assert.Equal("not_found", deletedAgain.ErrorCode);
            Assert.Equal("invalid_argument", unknownField.ErrorCode);
        }
    }

    /// <summary>History is grouped by series, with a series detail, bulk completion, deletion, clearing and a retention setting.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task HistoryOperations()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.InstallFixtureAsync().ConfigureAwait(true);
            host.Call("catalog.search", Query("series"));
            JsonArray children = host.Call("catalog.children", new JsonObject { ["ref"] = Series, ["limit"] = 3 }).Ok["items"]!.AsArray();
            string first = children[0]!["ref"]!.GetValue<string>();
            string second = children[1]!["ref"]!.GetValue<string>();
            host.Call("progress.set", new JsonObject { ["ref"] = first, ["unit"] = "page", ["value"] = 4, ["total"] = 30 });

            CallResult completed = host.Call("progress.complete", new JsonObject { ["refs"] = new JsonArray(first, second) });
            CallResult history = host.Call("progress.history", new JsonObject { ["limit"] = 10 });
            CallResult series = host.Call("progress.series", new JsonObject { ["ref"] = Series });
            CallResult noRefs = host.Call("progress.complete", new JsonObject { ["refs"] = new JsonArray() });
            CallResult retentionBefore = host.Call("progress.retention_get");
            CallResult retention = host.Call("progress.retention_set", new JsonObject { ["days"] = 30 });
            CallResult tooLong = host.Call("progress.retention_set", new JsonObject { ["days"] = 99999 });
            CallResult noDays = host.Call("progress.retention_set");
            CallResult deleted = host.Call("progress.delete", new JsonObject { ["refs"] = new JsonArray(Series) });
            CallResult empty = host.Call("progress.history");
            host.Call("progress.set", new JsonObject { ["ref"] = first, ["unit"] = "page", ["value"] = 1 });
            CallResult cleared = host.Call("progress.clear");

            Assert.Equal(30, completed.Ok["items"]![0]!["value"]!.GetValue<double>());
            Assert.True(completed.Ok["items"]![1]!["completed"]!.GetValue<bool>());
            Assert.Equal(Series, history.Ok["groups"]![0]!["series"]!.GetValue<string>());
            Assert.Equal(2, history.Ok["groups"]![0]!["completed"]!.GetValue<int>());
            Assert.Equal(0, history.Ok["groups"]![0]!["in_progress"]!.GetValue<int>());
            Assert.Equal(2, series.Ok["items"]!.AsArray().Count);
            Assert.Equal(2, series.Ok["completed"]!.GetValue<int>());
            Assert.Equal("invalid_argument", noRefs.ErrorCode);
            Assert.Equal(0, retentionBefore.Ok["days"]!.GetValue<int>());
            Assert.Equal(30, retention.Ok["days"]!.GetValue<int>());
            Assert.Equal("invalid_argument", tooLong.ErrorCode);
            Assert.Equal("invalid_argument", noDays.ErrorCode);
            Assert.Equal(2, deleted.Ok["removed"]!.GetValue<int>());
            Assert.Empty(empty.Ok["groups"]!.AsArray());
            Assert.Equal(1, cleared.Ok["removed"]!.GetValue<int>());
        }
    }

    /// <summary>The app remembers small choices under a scope; the host's own scopes are refused.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RememberedChoices()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            JsonObject scope = new() { ["scope"] = $"series:{Series}" };

            CallResult empty = host.Call("app_settings.get", scope);
            CallResult set = host.Call(
                "app_settings.set",
                new JsonObject
                {
                    ["scope"] = $"series:{Series}",
                    ["changes"] = new JsonArray(new JsonObject { ["key"] = "reverse", ["value"] = "1" }, new JsonObject { ["key"] = "groups", ["value"] = "[]" }),
                });
            CallResult forget = host.Call(
                "app_settings.set",
                new JsonObject
                {
                    ["scope"] = $"series:{Series}",
                    ["changes"] = new JsonArray(new JsonObject { ["key"] = "groups" }),
                });
            CallResult reserved = host.Call("app_settings.get", new JsonObject { ["scope"] = "emma.history" });
            CallResult noScope = host.Call("app_settings.get");
            CallResult noChanges = host.Call("app_settings.set", scope);

            Assert.Empty(empty.Ok["values"]!.AsObject());
            Assert.Equal(2, set.Ok["values"]!.AsObject().Count);
            Assert.Equal("1", forget.Ok["values"]!["reverse"]!.GetValue<string>());
            Assert.Single(forget.Ok["values"]!.AsObject());
            Assert.Equal("invalid_argument", reserved.ErrorCode);
            Assert.Equal("invalid_argument", noScope.ErrorCode);
            Assert.Equal("invalid_argument", noChanges.ErrorCode);
        }
    }

    /// <summary>Refreshing a library reports children the host had not seen, and a cover refresh and a plugin reload answer.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RefreshAndReload()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.InstallFixtureAsync().ConfigureAwait(true);
            host.Call("catalog.search", Query("series"));
            host.Call("catalog.children", new JsonObject { ["ref"] = Series, ["limit"] = 50 });
            host.Call("library.add", new JsonObject { ["ref"] = Series });

            CallResult refreshed = host.Call("library.refresh");
            CallResult again = host.Call("library.refresh", new JsonObject { ["library"] = "default" });
            CallResult missing = host.Call("library.refresh", new JsonObject { ["library"] = "Nowhere" });
            CallResult cover = host.Call("nodes.refresh_cover", new JsonObject { ["ref"] = Series });
            CallResult unseen = host.Call("nodes.refresh_cover", new JsonObject { ["ref"] = "com.example.fixture:never-seen" });
            CallResult noRef = host.Call("nodes.refresh_cover");
            CallResult reloaded = host.Call("plugins.reload");
            CallResult afterReload = host.Call("catalog.search", Query("series"));

            Assert.Equal(1, refreshed.Ok["checked"]!.GetValue<int>());

            // Browsing the series listed all of its children (the first page is only a slice of that), so a refresh
            // finds nothing the host had not already seen.
            Assert.Empty(refreshed.Ok["updated"]!.AsArray());
            Assert.Empty(refreshed.Ok["failed"]!.AsArray());
            Assert.Empty(again.Ok["updated"]!.AsArray());
            Assert.Equal("not_found", missing.ErrorCode);
            Assert.Equal(Series, cover.Ok["ref"]!.GetValue<string>());
            Assert.Equal("not_found", unseen.ErrorCode);
            Assert.Equal("invalid_argument", noRef.ErrorCode);
            Assert.Equal(NativeHarness.Plugin, reloaded.Ok["plugins"]![0]!["id"]!.GetValue<string>());
            Assert.NotNull(afterReload.Ok);
        }
    }

    private static JsonObject Query(string text) => new()
    {
        ["plugin"] = NativeHarness.Plugin,
        ["query"] = new JsonObject { ["text"] = text },
    };
}
