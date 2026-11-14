using System.Text.Json.Nodes;

using EMMA.PluginHost.Signing;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Cli.Tests;

/// <summary>Tests for scaffolding, packing, trusting, installing and running plugins through the command line.</summary>
public sealed class CliPluginTests
{
    private const string Id = "com.example.template";

    /// <summary>A new plugin gets the template with its names filled in and the contract it builds against, and never overwrites a project.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task NewScaffoldsATemplate()
    {
        string target = Path.Combine(Path.GetTempPath(), $"emma-cli-new-{Guid.NewGuid():N}");
        try
        {
            CliRun created = await CliRun.ExecuteAsync("plugin", "new", target, "--id", "com.acme.notes", "--name", "Notes").ConfigureAwait(true);
            CliRun again = await CliRun.ExecuteAsync("plugin", "new", target, "--id", "com.acme.notes").ConfigureAwait(true);
            CliRun badId = await CliRun.ExecuteAsync("plugin", "new", target + "-2", "--id", "Not An Id").ConfigureAwait(true);

            Assert.Equal(0, created.ExitCode);
            string manifest = await File.ReadAllTextAsync(Path.Combine(target, "plugin.json"), TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Contains("\"id\": \"com.acme.notes\"", manifest, StringComparison.Ordinal);
            Assert.Contains("\"name\": \"Notes\"", manifest, StringComparison.Ordinal);
            string cargo = await File.ReadAllTextAsync(Path.Combine(target, "Cargo.toml"), TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Contains("name = \"com_acme_notes\"", cargo, StringComparison.Ordinal);
            Assert.DoesNotContain("emma_plugin_template", cargo, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(target, "src", "lib.rs")));
            Assert.True(File.Exists(Path.Combine(target, ".gitignore")));

            // The build and pack script comes along and finds the renamed crate itself, so it holds no crate name.
            string script = await File.ReadAllTextAsync(Path.Combine(target, "build.sh"), TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Contains("plugin pack", script, StringComparison.Ordinal);
            Assert.DoesNotContain("emma_plugin_template", script, StringComparison.Ordinal);

            // The contract copied in is byte-for-byte the one this build understands.
            foreach (string contract in Directory.EnumerateFiles(Path.Combine(NativeArtifacts.RepositoryRoot(), "src", "EMMA.Contracts", "wit"), "*.wit"))
            {
                Assert.Equal(
                    await File.ReadAllTextAsync(contract, TestContext.Current.CancellationToken).ConfigureAwait(true),
                    await File.ReadAllTextAsync(Path.Combine(target, "wit", Path.GetFileName(contract)), TestContext.Current.CancellationToken).ConfigureAwait(true));
            }

            Assert.Equal(1, again.ExitCode);
            Assert.Equal(2, badId.ExitCode);
        }
        finally
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }

    /// <summary>Packing fills in the component's digest, validates the manifest as the installer will, and refuses what the installer would.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task PackBuildsAValidPackage()
    {
        DirectoryInfo plugin = await CliRun.PluginDirectoryAsync().ConfigureAwait(true);
        DirectoryInfo broken = await CliRun.PluginDirectoryAsync(manifest => manifest.Replace("\"cpuBudgetMs\": 5000", "\"cpuBudgetMs\": 0", StringComparison.Ordinal)).ConfigureAwait(true);
        DirectoryInfo missing = await CliRun.PluginDirectoryAsync().ConfigureAwait(true);
        File.Delete(Path.Combine(missing.FullName, "plugin.wasm"));
        string output = Path.Combine(Path.GetTempPath(), $"emma-cli-pack-{Guid.NewGuid():N}");
        try
        {
            CliRun packed = await CliRun.ExecuteAsync("plugin", "pack", plugin.FullName, "--out", output + "/").ConfigureAwait(true);
            CliRun invalid = await CliRun.ExecuteAsync("plugin", "pack", broken.FullName, "--out", output + "/").ConfigureAwait(true);
            CliRun noComponent = await CliRun.ExecuteAsync("plugin", "pack", missing.FullName, "--out", output + "/").ConfigureAwait(true);

            Assert.Equal(0, packed.ExitCode);
            (string[] entries, JsonNode manifest) = await CliRun.InspectAsync(Path.Combine(output, $"{Id}-0.1.0.zip")).ConfigureAwait(true);
            Assert.Equal(["plugin.json", "plugin.wasm"], entries);
            Assert.Equal(
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(plugin.FullName, "plugin.wasm"), TestContext.Current.CancellationToken).ConfigureAwait(true))),
                manifest["entrypoint"]!["sha256"]!.GetValue<string>());
            Assert.Null(manifest["signature"]);
            Assert.Equal(1, invalid.ExitCode);
            Assert.Contains("not valid", invalid.Error, StringComparison.Ordinal);
            Assert.Equal(1, noComponent.ExitCode);
            Assert.Contains("Build it first", noComponent.Error, StringComparison.Ordinal);
        }
        finally
        {
            plugin.Delete(recursive: true);
            broken.Delete(recursive: true);
            missing.Delete(recursive: true);
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    /// <summary>The whole journey: sign a package, trust the repository, install it, look at it, switch it off and on, and remove it.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SignedPackageJourney()
    {
        DirectoryInfo plugin = await CliRun.PluginDirectoryAsync().ConfigureAwait(true);
        DirectoryInfo data = Directory.CreateTempSubdirectory("emma-cli-data-");
        try
        {
            string key = Path.Combine(data.FullName, "signer.pem");
            await File.WriteAllTextAsync(key, TestPki.Signer.PrivatePem, TestContext.Current.CancellationToken).ConfigureAwait(true);
            await TrustTestRepositoryAsync(data.FullName).ConfigureAwait(true);

            CliRun packed = await CliRun.ExecuteAsync("plugin", "pack", plugin.FullName, "--out", data.FullName + "/", "--sign-key", key, "--key-id", TestPki.SignerKeyId, "--repository", TestPki.RepositoryId).ConfigureAwait(true);
            string package = Path.Combine(data.FullName, $"{Id}-0.1.0.zip");
            CliRun installed = await CliRun.ExecuteAsync("plugin", "install", package, "--repository", TestPki.RepositoryId, "--data-dir", data.FullName).ConfigureAwait(true);
            CliRun list = await CliRun.ExecuteAsync("plugin", "list", "--data-dir", data.FullName).ConfigureAwait(true);
            CliRun info = await CliRun.ExecuteAsync("plugin", "info", Id, "--data-dir", data.FullName).ConfigureAwait(true);
            CliRun disabled = await CliRun.ExecuteAsync("plugin", "disable", Id, "--data-dir", data.FullName).ConfigureAwait(true);
            CliRun listOff = await CliRun.ExecuteAsync("plugin", "list", "--json", "--data-dir", data.FullName).ConfigureAwait(true);
            CliRun enabled = await CliRun.ExecuteAsync("plugin", "enable", Id, "--data-dir", data.FullName).ConfigureAwait(true);
            CliRun removed = await CliRun.ExecuteAsync("plugin", "remove", Id, "--data-dir", data.FullName).ConfigureAwait(true);
            CliRun removedAgain = await CliRun.ExecuteAsync("plugin", "remove", Id, "--data-dir", data.FullName).ConfigureAwait(true);

            Assert.Equal(0, packed.ExitCode);
            Assert.Contains("signed by test-repo/signer-1", packed.Output, StringComparison.Ordinal);
            Assert.Equal(0, installed.ExitCode);
            Assert.Contains("test-repo/signer-1", list.Output, StringComparison.Ordinal);
            Assert.Contains("demo.collection", info.Output, StringComparison.Ordinal);
            Assert.Equal(0, disabled.ExitCode);
            Assert.Contains("\"status\": \"disabled\"", listOff.Output, StringComparison.Ordinal);
            Assert.Equal(0, enabled.ExitCode);
            Assert.Equal(0, removed.ExitCode);
            Assert.Equal(1, removedAgain.ExitCode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            plugin.Delete(recursive: true);
            data.Delete(recursive: true);
        }
    }

    /// <summary>A package from a repository nobody trusts is refused, and so is an unsigned one: install never downgrades.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UntrustedAndUnsignedPackagesAreRefused()
    {
        DirectoryInfo plugin = await CliRun.PluginDirectoryAsync().ConfigureAwait(true);
        DirectoryInfo data = Directory.CreateTempSubdirectory("emma-cli-data-");
        try
        {
            CliRun packed = await CliRun.ExecuteAsync("plugin", "pack", plugin.FullName, "--out", data.FullName + "/").ConfigureAwait(true);
            CliRun installed = await CliRun.ExecuteAsync("plugin", "install", Path.Combine(data.FullName, $"{Id}-0.1.0.zip"), "--repository", "anyone", "--data-dir", data.FullName).ConfigureAwait(true);
            CliRun list = await CliRun.ExecuteAsync("plugin", "list", "--data-dir", data.FullName).ConfigureAwait(true);

            Assert.Equal(0, packed.ExitCode);
            Assert.Equal(1, installed.ExitCode);
            Assert.Contains("No plugins installed.", list.Output, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            plugin.Delete(recursive: true);
            data.Delete(recursive: true);
        }
    }

    /// <summary>Running a plugin directory installs it unsigned in a throwaway host and answers every operation as the host would.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RunExercisesAPlugin()
    {
        DirectoryInfo plugin = await CliRun.PluginDirectoryAsync().ConfigureAwait(true);
        try
        {
            CliRun describe = await CliRun.ExecuteAsync("plugin", "run", plugin.FullName, "describe").ConfigureAwait(true);
            CliRun search = await CliRun.ExecuteAsync("plugin", "run", plugin.FullName, "search", "collection 2").ConfigureAwait(true);
            CliRun children = await CliRun.ExecuteAsync("plugin", "run", plugin.FullName, "children", "c1", "--limit", "2").ConfigureAwait(true);
            CliRun root = await CliRun.ExecuteAsync("plugin", "run", plugin.FullName, "children").ConfigureAwait(true);
            CliRun get = await CliRun.ExecuteAsync("plugin", "run", plugin.FullName, "get", "c1-e1", "nope").ConfigureAwait(true);
            CliRun assets = await CliRun.ExecuteAsync("plugin", "run", plugin.FullName, "assets", "c1-e2", "text").ConfigureAwait(true);
            CliRun content = await CliRun.ExecuteAsync("plugin", "run", plugin.FullName, "content", "c1-e2", "text").ConfigureAwait(true);
            CliRun unsupported = await CliRun.ExecuteAsync("plugin", "run", plugin.FullName, "assets", "c1", "text").ConfigureAwait(true);
            CliRun unknown = await CliRun.ExecuteAsync("plugin", "run", plugin.FullName, "dance").ConfigureAwait(true);

            Assert.Equal(0, describe.ExitCode);
            Assert.Contains("demo.collection", describe.Output, StringComparison.Ordinal);
            Assert.Contains("\"title\": \"Collection 2\"", search.Output, StringComparison.Ordinal);
            Assert.Contains("com.example.template:c1-e1", children.Output, StringComparison.Ordinal);
            Assert.Contains("\"next_cursor\": \"2\"", children.Output, StringComparison.Ordinal);
            Assert.Contains("com.example.template:c3", root.Output, StringComparison.Ordinal);
            Assert.Contains("com.example.template:c1-e1", get.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("nope", get.Output, StringComparison.Ordinal);
            Assert.Contains("\"delivery\": \"inline\"", assets.Output, StringComparison.Ordinal);
            Assert.Contains("bytes of text/plain", content.Output, StringComparison.Ordinal);
            Assert.Equal(1, unsupported.ExitCode);
            Assert.Contains("unsupported", unsupported.Error, StringComparison.Ordinal);
            Assert.Equal(2, unknown.ExitCode);
        }
        finally
        {
            plugin.Delete(recursive: true);
        }
    }

    /// <summary>A manifest that misdescribes the component is reported with the reason, which is the point of running before publishing.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RunReportsContractMismatches()
    {
        DirectoryInfo plugin = await CliRun.PluginDirectoryAsync(manifest => manifest.Replace("\"demo.entry\"", "\"demo.other\"", StringComparison.Ordinal)).ConfigureAwait(true);
        try
        {
            CliRun describe = await CliRun.ExecuteAsync("plugin", "run", plugin.FullName, "describe").ConfigureAwait(true);

            Assert.Equal(1, describe.ExitCode);
            Assert.Contains("kinds", describe.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            plugin.Delete(recursive: true);
        }
    }

    private static async Task TrustTestRepositoryAsync(string dataDirectory)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DelegatedSigner signer = TestPki.Delegate(["com.example.*"], validFrom: now.AddDays(-30), validUntil: now.AddDays(30));
        string delegations = Path.Combine(dataDirectory, "delegations");
        Directory.CreateDirectory(delegations);
        await File.WriteAllBytesAsync(Path.Combine(delegations, $"{TestPki.RepositoryId}.delegations.json"), DelegationJson.Write(TestPki.Bundle(1, signer))).ConfigureAwait(false);
        JsonObject config = new()
        {
            ["trust"] = new JsonObject
            {
                ["repositories"] = new JsonArray(new JsonObject
                {
                    ["id"] = TestPki.RepositoryId,
                    ["root_keys"] = new JsonObject { [TestPki.RootKeyId] = TestPki.Root.PublicPem },
                }),
            },
        };
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "config.json"), config.ToJsonString()).ConfigureAwait(false);
    }
}
