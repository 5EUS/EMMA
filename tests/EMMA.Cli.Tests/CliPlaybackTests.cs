using System.Text.Json.Nodes;

using EMMA.Domain;
using EMMA.Runtime;

using Xunit;

namespace EMMA.Cli.Tests;

/// <summary>Tests for the playback commands, against the real fixture plugin running as Wasm.</summary>
public sealed class CliPlaybackTests
{
    private const string Id = "com.example.fixture";
    private const string Track = "com.example.fixture:t1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A plan is shown as text and as JSON in the shape the API uses.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task APlanIsShownAsTextAndAsJson() => WithSeenTrackAsync(async data =>
    {
        CliRun text = await CliRun.ExecuteAsync("playback", "plan", Track, "--data-dir", data).ConfigureAwait(true);
        CliRun json = await CliRun.ExecuteAsync("playback", "plan", Track, "--json", "--data-dir", data).ConfigureAwait(true);

        Assert.Equal(0, text.ExitCode);
        Assert.Contains("plays as 'audio' (0:03:20)", text.Output, StringComparison.Ordinal);
        Assert.Contains("[0] audio/mpeg", text.Output, StringComparison.Ordinal);
        Assert.Contains("0:01:40  Side B", text.Output, StringComparison.Ordinal);
        Assert.Equal("audio", JsonNode.Parse(json.Output)!["primary"]!.GetValue<string>());
        Assert.Equal(2, JsonNode.Parse(json.Output)!["chapters"]!.AsArray().Count);
    });

    /// <summary>Serving prints an address that a player can use, keeps serving until interrupted, and then ends cleanly.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ServingGivesAPlayableAddressUntilInterrupted() => WithSeenTrackAsync(async data =>
    {
        using CancellationTokenSource interrupt = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using StringWriter output = new();
        using StringWriter error = new();

        // The finally block below awaits the command before the writers go out of scope; the analyzer cannot see that through the try.
#pragma warning disable CA2025
        Task<int> serving = CliApp.RunAsync(["playback", "serve", Track, "--role", "audio", "--data-dir", data], output, error, interrupt.Token);
#pragma warning restore CA2025
        int exit;
        byte[] bytes;
        try
        {
            Uri? url = null;
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            while (url is null)
            {
                await Task.Delay(50, limit.Token).ConfigureAwait(true);
                string first = output.ToString().Split('\n')[0].Trim();
                url = first.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) ? new Uri(first.Split(' ')[0]) : null;
            }

            using HttpClient http = new();
            bytes = await http.GetByteArrayAsync(url, Ct).ConfigureAwait(true);
        }
        finally
        {
            await interrupt.CancelAsync().ConfigureAwait(true);
            exit = await serving.ConfigureAwait(true);
        }

        Assert.Equal(1000, bytes.Length);
        Assert.Equal(0, exit);
        Assert.Contains("(audio/mpeg)", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    });

    /// <summary>A node the host never saw, a node with nothing to play, and a command line that is wrong are refused.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task RefusesWhatCannotBePlayed() => WithSeenTrackAsync(async data =>
    {
        CliRun unseen = await CliRun.ExecuteAsync("playback", "plan", "com.example.fixture:nope", "--data-dir", data).ConfigureAwait(true);
        CliRun badRef = await CliRun.ExecuteAsync("playback", "plan", "nonsense", "--data-dir", data).ConfigureAwait(true);
        CliRun badHint = await CliRun.ExecuteAsync("playback", "plan", Track, "--hint", string.Empty, "--data-dir", data).ConfigureAwait(true);
        CliRun badRole = await CliRun.ExecuteAsync("playback", "serve", Track, "--role", "Not A Role", "--data-dir", data).ConfigureAwait(true);
        CliRun badIndex = await CliRun.ExecuteAsync("playback", "serve", Track, "--index", "many", "--data-dir", data).ConfigureAwait(true);
        CliRun noIndex = await CliRun.ExecuteAsync("playback", "serve", Track, "--role", "audio", "--index", "7", "--data-dir", data).ConfigureAwait(true);
        CliRun missing = await CliRun.ExecuteAsync("playback", "plan", "--data-dir", data).ConfigureAwait(true);

        Assert.Equal(1, unseen.ExitCode);
        Assert.Contains("not been seen", unseen.Error, StringComparison.Ordinal);
        Assert.Equal(2, badRef.ExitCode);
        Assert.Equal(2, badHint.ExitCode);
        Assert.Equal(2, badRole.ExitCode);
        Assert.Equal(2, badIndex.ExitCode);
        Assert.Equal(1, noIndex.ExitCode);
        Assert.Equal(2, missing.ExitCode);
    });

    private static async Task WithSeenTrackAsync(Func<string, Task> test)
    {
        DirectoryInfo data = Directory.CreateTempSubdirectory("emma-cli-playback-");
        try
        {
            await CliRun.InstallUnsignedFixtureAsync(data.FullName, Id, Ct).ConfigureAwait(false);

            // The command line has no search, so the node is recorded the way an app's search would have.
            EmmaRuntime runtime = new(new EmmaRuntimeOptions(data.FullName));
            await using (runtime.ConfigureAwait(false))
            {
                await runtime.InitializeAsync(Ct).ConfigureAwait(false);
                await runtime.Nodes.UpsertAsync([new Node(NodeRef.Parse(Track), NodeKind.Create("track"), "Track 1", null, null, [])], Ct).ConfigureAwait(false);
            }

            await test(data.FullName).ConfigureAwait(false);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            data.Delete(recursive: true);
        }
    }
}
