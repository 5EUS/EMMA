using System.Text.Json.Nodes;

using Xunit;

namespace EMMA.Cli.Tests;

/// <summary>Tests for the download commands: the queue, the limit, and what a command line refuses.</summary>
public sealed class CliDownloadTests
{
    private const string Chapter = "com.example.fixture:c1";

    /// <summary>An empty queue lists as nothing and works as nothing; both are the shapes the API uses with <c>--json</c>.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task AnEmptyQueue() => WithDataAsync(async data =>
    {
        CliRun list = await CliRun.ExecuteAsync("downloads", "list", "--data-dir", data).ConfigureAwait(true);
        CliRun json = await CliRun.ExecuteAsync("downloads", "list", "--json", "--data-dir", data).ConfigureAwait(true);
        CliRun retry = await CliRun.ExecuteAsync("downloads", "retry-failed", "--data-dir", data).ConfigureAwait(true);
        CliRun clear = await CliRun.ExecuteAsync("downloads", "clear", "failed", "canceled", "--data-dir", data).ConfigureAwait(true);
        CliRun run = await CliRun.ExecuteAsync("downloads", "run", "--data-dir", data).ConfigureAwait(true);

        Assert.Equal(0, list.ExitCode);
        Assert.Contains("No downloads.", list.Output, StringComparison.Ordinal);
        Assert.Empty(JsonNode.Parse(json.Output)!["items"]!.AsArray());
        Assert.Contains("Queued 0", retry.Output, StringComparison.Ordinal);
        Assert.Contains("Deleted 0", clear.Output, StringComparison.Ordinal);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("The queue is empty.", run.Output, StringComparison.Ordinal);
    });

    /// <summary>The limit is shown, set within its bounds and remembered between commands.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task TheParallelLimit() => WithDataAsync(async data =>
    {
        CliRun initial = await CliRun.ExecuteAsync("downloads", "parallel", "--data-dir", data).ConfigureAwait(true);
        CliRun set = await CliRun.ExecuteAsync("downloads", "parallel", "4", "--data-dir", data).ConfigureAwait(true);
        CliRun json = await CliRun.ExecuteAsync("downloads", "parallel", "--json", "--data-dir", data).ConfigureAwait(true);
        CliRun tooMany = await CliRun.ExecuteAsync("downloads", "parallel", "99", "--data-dir", data).ConfigureAwait(true);
        CliRun notANumber = await CliRun.ExecuteAsync("downloads", "parallel", "many", "--data-dir", data).ConfigureAwait(true);

        Assert.Contains("2 download(s)", initial.Output, StringComparison.Ordinal);
        Assert.Contains("4 download(s)", set.Output, StringComparison.Ordinal);
        Assert.Equal(4, JsonNode.Parse(json.Output)!["parallel"]!.GetValue<int>());
        Assert.Equal(1, tooMany.ExitCode);
        Assert.Equal(2, notANumber.ExitCode);
    });

    /// <summary>A download of a node the host never served, or without a role, or of an unknown id, is refused.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task RefusesWhatCannotWork() => WithDataAsync(async data =>
    {
        CliRun unseen = await CliRun.ExecuteAsync("downloads", "add", Chapter, "--role", "image", "--data-dir", data).ConfigureAwait(true);
        CliRun noRole = await CliRun.ExecuteAsync("downloads", "add", Chapter, "--data-dir", data).ConfigureAwait(true);
        CliRun badRef = await CliRun.ExecuteAsync("downloads", "add", "nonsense", "--role", "image", "--data-dir", data).ConfigureAwait(true);
        CliRun badHint = await CliRun.ExecuteAsync("downloads", "add", Chapter, "--role", "image", "--hint", string.Empty, "--data-dir", data).ConfigureAwait(true);
        CliRun badId = await CliRun.ExecuteAsync("downloads", "pause", "not-an-id", "--data-dir", data).ConfigureAwait(true);
        CliRun unknownId = await CliRun.ExecuteAsync("downloads", "resume", Guid.NewGuid().ToString(), "--data-dir", data).ConfigureAwait(true);
        CliRun deleteUnknown = await CliRun.ExecuteAsync("downloads", "delete", Guid.NewGuid().ToString(), "--data-dir", data).ConfigureAwait(true);
        CliRun clearNothing = await CliRun.ExecuteAsync("downloads", "clear", "--data-dir", data).ConfigureAwait(true);
        CliRun clearBogus = await CliRun.ExecuteAsync("downloads", "clear", "bogus", "--data-dir", data).ConfigureAwait(true);

        Assert.Equal(1, unseen.ExitCode);
        Assert.Contains("not served", unseen.Error, StringComparison.Ordinal);
        Assert.Equal(2, noRole.ExitCode);
        Assert.Equal(2, badRef.ExitCode);
        Assert.Equal(2, badHint.ExitCode);
        Assert.Equal(2, badId.ExitCode);
        Assert.Equal(1, unknownId.ExitCode);
        Assert.Equal(1, deleteUnknown.ExitCode);
        Assert.Equal(2, clearNothing.ExitCode);
        Assert.Equal(2, clearBogus.ExitCode);
    });

    private static async Task WithDataAsync(Func<string, Task> test)
    {
        DirectoryInfo data = Directory.CreateTempSubdirectory("emma-cli-downloads-");
        try
        {
            await test(data.FullName).ConfigureAwait(false);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            data.Delete(recursive: true);
        }
    }
}
