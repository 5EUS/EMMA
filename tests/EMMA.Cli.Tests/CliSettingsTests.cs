using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Xunit;

namespace EMMA.Cli.Tests;

/// <summary>Tests for reading and changing a plugin's settings through the command line.</summary>
public sealed partial class CliSettingsTests
{
    private const string Id = "com.example.fixture";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The whole journey: see what a plugin declares, set values, give a secret on standard input, clear one.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task SetShowAndClear() => WithInstalledFixtureAsync(async data =>
    {
        CliRun fresh = await CliRun.ExecuteAsync("plugin", "settings", Id, "--data-dir", data).ConfigureAwait(true);
        CliRun user = await CliRun.ExecuteAsync("plugin", "settings", Id, "set", "connection.user", "me", "--data-dir", data).ConfigureAwait(true);
        CliRun language = await CliRun.ExecuteAsync("plugin", "settings", Id, "set", "search.language", "ja", "--data-dir", data).ConfigureAwait(true);
        CliRun secret = await CliRun.ExecuteWithInputAsync("hunter2\nsecond line ignored\n", "plugin", "settings", Id, "set-secret", "connection.token", "--data-dir", data).ConfigureAwait(true);
        CliRun after = await CliRun.ExecuteAsync("plugin", "settings", Id, "--data-dir", data).ConfigureAwait(true);
        CliRun cleared = await CliRun.ExecuteAsync("plugin", "settings", Id, "clear", "connection.user", "--data-dir", data).ConfigureAwait(true);
        CliRun json = await CliRun.ExecuteAsync("plugin", "settings", Id, "--json", "--data-dir", data).ConfigureAwait(true);

        Assert.Contains("connection.token", fresh.Output, StringComparison.Ordinal);
        Assert.Contains("REQUIRED", fresh.Output, StringComparison.Ordinal);
        Assert.Contains("en (default)", fresh.Output, StringComparison.Ordinal);
        Assert.Equal(0, user.ExitCode);
        Assert.Equal(0, language.ExitCode);
        Assert.Equal(0, secret.ExitCode);
        Assert.Contains("connection.user", after.Output, StringComparison.Ordinal);
        Assert.Matches(UserLine(), after.Output);
        Assert.DoesNotContain("hunter2", after.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("REQUIRED", after.Output, StringComparison.Ordinal);
        Assert.Equal(0, cleared.ExitCode);
        Assert.DoesNotContain("hunter2", json.Output, StringComparison.Ordinal);
        JsonNode states = JsonNode.Parse(json.Output)!["settings"]!;
        Assert.False(states.AsArray().Single(state => string.Equals(state!["key"]!.GetValue<string>(), "connection.user", StringComparison.Ordinal))!["is_set"]!.GetValue<bool>());
    });

    /// <summary>A secret is never taken from the command line, where it would land in shell history.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task SecretsAreNotTakenOnTheCommandLine() => WithInstalledFixtureAsync(async data =>
    {
        CliRun onCommandLine = await CliRun.ExecuteAsync("plugin", "settings", Id, "set", "connection.token", "hunter2", "--data-dir", data).ConfigureAwait(true);
        CliRun notASecret = await CliRun.ExecuteWithInputAsync("x\n", "plugin", "settings", Id, "set-secret", "connection.user", "--data-dir", data).ConfigureAwait(true);
        CliRun noInput = await CliRun.ExecuteWithInputAsync(string.Empty, "plugin", "settings", Id, "set-secret", "connection.token", "--data-dir", data).ConfigureAwait(true);

        Assert.Equal(2, onCommandLine.ExitCode);
        Assert.Contains("set-secret", onCommandLine.Error, StringComparison.Ordinal);
        Assert.Equal(2, notASecret.ExitCode);
        Assert.Equal(2, noInput.ExitCode);
    });

    /// <summary>A value the plugin did not declare room for is refused with the reason, and the exit code says it failed.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task BadValuesAreRefused() => WithInstalledFixtureAsync(async data =>
    {
        CliRun wrongType = await CliRun.ExecuteAsync("plugin", "settings", Id, "set", "advanced.verbose", "maybe", "--data-dir", data).ConfigureAwait(true);
        CliRun undeclared = await CliRun.ExecuteAsync("plugin", "settings", Id, "set", "nope", "x", "--data-dir", data).ConfigureAwait(true);
        CliRun twoValues = await CliRun.ExecuteAsync("plugin", "settings", Id, "set", "search.language", "en", "ja", "--data-dir", data).ConfigureAwait(true);
        CliRun missingValue = await CliRun.ExecuteAsync("plugin", "settings", Id, "set", "connection.user", "--data-dir", data).ConfigureAwait(true);
        CliRun notInstalled = await CliRun.ExecuteAsync("plugin", "settings", "com.example.nope", "--data-dir", data).ConfigureAwait(true);

        Assert.Equal(1, wrongType.ExitCode);
        Assert.Contains("advanced.verbose", wrongType.Error, StringComparison.Ordinal);
        Assert.Equal(1, undeclared.ExitCode);
        Assert.Equal(1, twoValues.ExitCode);
        Assert.Equal(2, missingValue.ExitCode);
        Assert.Equal(1, notInstalled.ExitCode);
    });

    /// <summary><c>plugin run --settings</c> gives the plugin values for its settings, so an author can try it configured.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RunCanBeGivenSettings()
    {
        DirectoryInfo plugin = await CliRun.PluginDirectoryAsync(NoKinds, component: "fixture").ConfigureAwait(true);
        string file = Path.Combine(plugin.FullName, "settings.json");
        try
        {
            await File.WriteAllTextAsync(file, @"{""connection.user"":""author"",""connection.token"":""t0ken"",""advanced.verbose"":true}", Ct).ConfigureAwait(true);

            CliRun configured = await CliRun.ExecuteAsync("plugin", "run", plugin.FullName, "search", "setting:connection.user", "--settings", file).ConfigureAwait(true);
            CliRun unconfigured = await CliRun.ExecuteAsync("plugin", "run", plugin.FullName, "search", "setting:connection.user").ConfigureAwait(true);

            Assert.Equal(0, configured.ExitCode);
            Assert.Equal("author", Title(configured));
            Assert.Equal("<none>", Title(unconfigured));
        }
        finally
        {
            plugin.Delete(recursive: true);
        }
    }

    /// <summary>A settings file the plugin cannot take is reported before anything runs.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RunRefusesAnUnusableSettingsFile()
    {
        DirectoryInfo plugin = await CliRun.PluginDirectoryAsync(NoKinds, component: "fixture").ConfigureAwait(true);
        try
        {
            string undeclared = Path.Combine(plugin.FullName, "undeclared.json");
            string wrongType = Path.Combine(plugin.FullName, "wrong-type.json");
            string notJson = Path.Combine(plugin.FullName, "not-json.json");
            await File.WriteAllTextAsync(undeclared, @"{""nope"":""x""}", Ct).ConfigureAwait(true);
            await File.WriteAllTextAsync(wrongType, @"{""advanced.verbose"":""maybe""}", Ct).ConfigureAwait(true);
            await File.WriteAllTextAsync(notJson, "{", Ct).ConfigureAwait(true);

            CliRun first = await CliRun.ExecuteAsync("plugin", "run", plugin.FullName, "describe", "--settings", undeclared).ConfigureAwait(true);
            CliRun second = await CliRun.ExecuteAsync("plugin", "run", plugin.FullName, "describe", "--settings", wrongType).ConfigureAwait(true);
            CliRun third = await CliRun.ExecuteAsync("plugin", "run", plugin.FullName, "describe", "--settings", notJson).ConfigureAwait(true);
            CliRun fourth = await CliRun.ExecuteAsync("plugin", "run", plugin.FullName, "describe", "--settings", Path.Combine(plugin.FullName, "missing.json")).ConfigureAwait(true);

            Assert.Equal(1, first.ExitCode);
            Assert.Contains("nope", first.Error, StringComparison.Ordinal);
            Assert.Equal(1, second.ExitCode);
            Assert.Equal(1, third.ExitCode);
            Assert.Equal(1, fourth.ExitCode);
        }
        finally
        {
            plugin.Delete(recursive: true);
        }
    }

    private static string Title(CliRun run) => JsonNode.Parse(run.Output)!["items"]![0]!["title"]!.GetValue<string>();

    [GeneratedRegex(@"connection\.user\s+text\s+me\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UserLine();

    // The fixture declares no kinds in its manifest (the test packages leave them out), unlike the template.
    private static string NoKinds(string manifest) => KindsLine().Replace(manifest, string.Empty);

    [GeneratedRegex(@"""kinds"":\s*\[[^\]]*\],\s*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KindsLine();

    private static async Task WithInstalledFixtureAsync(Func<string, Task> test)
    {
        DirectoryInfo data = Directory.CreateTempSubdirectory("emma-cli-settings-");
        try
        {
            await CliRun.InstallUnsignedFixtureAsync(data.FullName, Id, Ct).ConfigureAwait(false);

            await test(data.FullName).ConfigureAwait(false);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            data.Delete(recursive: true);
        }
    }
}
