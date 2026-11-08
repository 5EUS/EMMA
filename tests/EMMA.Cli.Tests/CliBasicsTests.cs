using Xunit;

namespace EMMA.Cli.Tests;

/// <summary>Tests for the command line's shape: help, usage errors and exit codes.</summary>
public sealed class CliBasicsTests
{
    /// <summary>Help succeeds; running with nothing to do prints it but reports wrong usage.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task HelpAndEmptyCommandLine()
    {
        CliRun help = await CliRun.ExecuteAsync("--help").ConfigureAwait(true);
        CliRun empty = await CliRun.ExecuteAsync().ConfigureAwait(true);

        Assert.Equal(0, help.ExitCode);
        Assert.Contains("emma plugin install", help.Output, StringComparison.Ordinal);
        Assert.Equal(2, empty.ExitCode);
        Assert.Contains("Usage:", empty.Output, StringComparison.Ordinal);
    }

    /// <summary>Unknown options, unknown commands and options missing their value are usage errors.</summary>
    /// <param name="args">The command line.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("--surprise")]
    [InlineData("plugin", "frobnicate")]
    [InlineData("plugin", "info")]
    [InlineData("plugin", "list", "--data-dir")]
    [InlineData("plugin", "info", "NOT AN ID")]
    [InlineData("plugin", "install", "x.zip")]
    public async Task UsageErrors(params string[] args)
    {
        CliRun run = await CliRun.ExecuteAsync(args).ConfigureAwait(true);

        Assert.Equal(2, run.ExitCode);
        Assert.StartsWith("error:", run.Error, StringComparison.Ordinal);
    }

    /// <summary>An empty data directory lists no plugins, as text or JSON.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task EmptyListing()
    {
        DirectoryInfo data = Directory.CreateTempSubdirectory("emma-cli-data-");
        try
        {
            CliRun text = await CliRun.ExecuteAsync("plugin", "list", "--data-dir", data.FullName).ConfigureAwait(true);
            CliRun json = await CliRun.ExecuteAsync("plugin", "list", "--json", "--data-dir", data.FullName).ConfigureAwait(true);

            Assert.Equal(0, text.ExitCode);
            Assert.Contains("No plugins installed.", text.Output, StringComparison.Ordinal);
            Assert.Contains("\"plugins\": []", json.Output, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            data.Delete(recursive: true);
        }
    }
}
