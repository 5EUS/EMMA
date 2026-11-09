using System.Text.Json.Nodes;

using Xunit;

namespace EMMA.Native.Tests;

/// <summary>Tests the download operations through the library, against the real fixture plugin running as Wasm.</summary>
public sealed class NativeDownloadTests
{
    private const string Probe = "com.example.fixture:probe";

    private static readonly JsonObject Anything = new()
    {
        ["plugin"] = NativeHarness.Plugin,
        ["query"] = new JsonObject { ["text"] = "anything" },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A node is downloaded end to end by the library's own executor, and then read with the plugin uninstalled.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ADownloadSurvivesUninstallingThePlugin()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.InstallFixtureAsync().ConfigureAwait(true);
            host.Call("catalog.search", Anything);

            CallResult queued = host.Call("downloads.enqueue", new JsonObject { ["ref"] = Probe, ["role"] = "blob" });
            string id = queued.Ok["id"]!.GetValue<string>();
            JsonNode detail = await WaitForAsync(host, id, "completed").ConfigureAwait(true);
            byte[] live = host.Call("assets.content", new JsonObject { ["ref"] = Probe, ["role"] = "blob", ["range"] = new JsonObject { ["start"] = 100, ["length"] = 50 } }).Blob!;
            host.Call("plugins.uninstall", new JsonObject { ["plugin"] = NativeHarness.Plugin });
            CallResult offline = host.Call("assets.content", new JsonObject { ["ref"] = Probe, ["role"] = "blob", ["range"] = new JsonObject { ["start"] = 100, ["length"] = 50 } });
            CallResult whole = host.Call("assets.content", new JsonObject { ["ref"] = Probe, ["role"] = "blob" });

            Assert.Equal("queued", queued.Ok["state"]!.GetValue<string>());
            Assert.Equal(1, detail["download"]!["items_completed"]!.GetValue<int>());
            Assert.Equal(5 * 1024 * 1024, detail["download"]!["bytes_downloaded"]!.GetValue<long>());
            Assert.Equal(Probe, detail["items"]![0]!["ref"]!.GetValue<string>());
            Assert.Equal(live, offline.Blob);
            Assert.Equal(100, offline.Ok["range_start"]!.GetValue<long>());
            Assert.Equal(5 * 1024 * 1024, offline.Ok["total"]!.GetValue<long>());
            Assert.Equal(5 * 1024 * 1024, whole.Blob!.Length);
        }
    }

    /// <summary>A job the plugin cannot serve fails with a reason, and the controls work on it.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task FailureControlsAndBulkOperations()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.InstallFixtureAsync().ConfigureAwait(true);
            host.Call("catalog.search", Anything);

            // The fixture resolves a probe for 'blob' and 'text' but has nothing for 'image'.
            string id = host.Call("downloads.enqueue", new JsonObject { ["ref"] = Probe, ["role"] = "image", ["hint"] = "high" }).Ok["id"]!.GetValue<string>();
            JsonNode failed = await WaitForAsync(host, id, "failed").ConfigureAwait(true);
            CallResult listed = host.Call("downloads.list", new JsonObject { ["states"] = new JsonArray("failed") });
            CallResult listedNone = host.Call("downloads.list", new JsonObject { ["states"] = new JsonArray("completed"), ["limit"] = 5 });
            CallResult pauseFailed = host.Call("downloads.pause", new JsonObject { ["id"] = id });
            CallResult clearNothing = host.Call("downloads.clear", new JsonObject { ["states"] = new JsonArray() });
            CallResult clearBogus = host.Call("downloads.clear", new JsonObject { ["states"] = new JsonArray("bogus") });
            CallResult retryAll = host.Call("downloads.retry_failed");
            await WaitForAsync(host, id, "failed").ConfigureAwait(true);
            CallResult cancelFailed = host.Call("downloads.cancel", new JsonObject { ["id"] = id });
            CallResult cleared = host.Call("downloads.clear", new JsonObject { ["states"] = new JsonArray("failed") });
            CallResult deleteGone = host.Call("downloads.delete", new JsonObject { ["id"] = id });
            CallResult getGone = host.Call("downloads.get", new JsonObject { ["id"] = id });

            Assert.Equal("high", failed["download"]!["hint"]!.GetValue<string>());
            Assert.False(string.IsNullOrEmpty(failed["download"]!["error"]!.GetValue<string>()));
            Assert.Equal(0, failed["download"]!["items_completed"]!.GetValue<int>());
            Assert.Single(listed.Ok["items"]!.AsArray());
            Assert.Empty(listedNone.Ok["items"]!.AsArray());
            Assert.Equal("invalid_argument", pauseFailed.ErrorCode);
            Assert.Equal("invalid_argument", clearNothing.ErrorCode);
            Assert.Equal("invalid_argument", clearBogus.ErrorCode);
            Assert.Equal(1, retryAll.Ok["count"]!.GetValue<int>());
            Assert.Equal("invalid_argument", cancelFailed.ErrorCode);
            Assert.Equal(1, cleared.Ok["count"]!.GetValue<int>());
            Assert.Equal("not_found", deleteGone.ErrorCode);
            Assert.Equal("not_found", getGone.ErrorCode);
        }
    }

    /// <summary>Requests that are malformed or name nothing the host has served are refused, and the parallel limit is bounded.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ValidationAndSettings()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.InstallFixtureAsync().ConfigureAwait(true);

            CallResult unseen = host.Call("downloads.enqueue", new JsonObject { ["ref"] = Probe, ["role"] = "blob" });
            CallResult noRole = host.Call("downloads.enqueue", new JsonObject { ["ref"] = Probe });
            CallResult badHint = host.Call("downloads.enqueue", new JsonObject { ["ref"] = Probe, ["role"] = "blob", ["hint"] = string.Empty });
            CallResult unknownField = host.Call("downloads.enqueue", new JsonObject { ["ref"] = Probe, ["role"] = "blob", ["quality"] = "x" });
            CallResult badId = host.Call("downloads.get", new JsonObject { ["id"] = "nope" });
            CallResult noId = host.Call("downloads.resume", new JsonObject());
            CallResult defaults = host.Call("downloads.settings_get");
            CallResult set = host.Call("downloads.settings_set", new JsonObject { ["parallel"] = 4 });
            CallResult after = host.Call("downloads.settings_get");
            CallResult tooMany = host.Call("downloads.settings_set", new JsonObject { ["parallel"] = 99 });
            CallResult missing = host.Call("downloads.settings_set", new JsonObject());
            CallResult hintOnAssets = host.Call("assets.list", new JsonObject { ["ref"] = "com.example.fixture:inline1", ["role"] = "text", ["hint"] = string.Empty });
            CallResult empty = host.Call("downloads.list");

            Assert.Equal("not_found", unseen.ErrorCode);
            Assert.Equal("invalid_argument", noRole.ErrorCode);
            Assert.Equal("invalid_argument", badHint.ErrorCode);
            Assert.Equal("invalid_argument", unknownField.ErrorCode);
            Assert.Equal("invalid_argument", badId.ErrorCode);
            Assert.Equal("invalid_argument", noId.ErrorCode);
            Assert.Equal(2, defaults.Ok["parallel"]!.GetValue<int>());
            Assert.Equal(4, set.Ok["parallel"]!.GetValue<int>());
            Assert.Equal(4, after.Ok["parallel"]!.GetValue<int>());
            Assert.Equal("invalid_argument", tooMany.ErrorCode);
            Assert.Equal("invalid_argument", missing.ErrorCode);
            Assert.Equal("invalid_argument", hintOnAssets.ErrorCode);
            Assert.Empty(empty.Ok["items"]!.AsArray());
        }
    }

    private static async Task<JsonNode> WaitForAsync(NativeHarness host, string id, string state)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            JsonNode detail = host.Call("downloads.get", new JsonObject { ["id"] = id }).Ok;
            if (string.Equals(detail["download"]!["state"]!.GetValue<string>(), state, StringComparison.Ordinal))
            {
                return detail;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), limit.Token).ConfigureAwait(false);
        }
    }
}
