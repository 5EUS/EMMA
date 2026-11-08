using EMMA.Domain;
using EMMA.PluginHost.Signing;
using EMMA.TestSupport;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace EMMA.Runtime.Tests;

/// <summary>A runtime over a temporary data directory, trusting the test repository, with its delegation file in place.</summary>
internal sealed class RuntimeHarness : IAsyncDisposable
{
    /// <summary>The id the fixture plugin is installed under.</summary>
    internal const string Plugin = "com.example.fixture";

    private readonly DirectoryInfo root;

    private RuntimeHarness(DirectoryInfo root)
    {
        this.root = root;
        this.Runtime = this.Open();
    }

    /// <summary>Gets the current runtime; <see cref="ReopenAsync"/> replaces it.</summary>
    internal EmmaRuntime Runtime { get; private set; }

    /// <summary>Gets the directory the runtime keeps its state in.</summary>
    internal string DataDirectory => this.root.FullName;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await this.Runtime.DisposeAsync().ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        this.root.Delete(recursive: true);
    }

    /// <summary>Creates the data directory and the delegation file, and opens a runtime over it.</summary>
    /// <returns>The harness.</returns>
    internal static async Task<RuntimeHarness> StartAsync()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("emma-runtime-test-");
        string delegations = Path.Combine(directory.FullName, "delegations");
        Directory.CreateDirectory(delegations);
        await File.WriteAllBytesAsync(Path.Combine(delegations, $"{TestPki.RepositoryId}.delegations.json"), DelegationJson.Write(TestPki.Bundle())).ConfigureAwait(false);
        return new RuntimeHarness(directory);
    }

    /// <summary>Opens another runtime over the same data directory.</summary>
    /// <param name="adjust">Changes the options before the runtime is built.</param>
    /// <returns>The runtime; the caller disposes it.</returns>
    internal EmmaRuntime Open(Func<EmmaRuntimeOptions, EmmaRuntimeOptions>? adjust = null)
    {
        EmmaRuntimeOptions options = new(this.DataDirectory) { Trust = TestPki.Policy(), Time = new FakeTimeProvider(TestPki.Now) };
        return new EmmaRuntime(adjust?.Invoke(options) ?? options);
    }

    /// <summary>Installs the fixture plugin from a package.</summary>
    /// <param name="sign">Whether the package is signed.</param>
    /// <returns>The install result.</returns>
    internal async Task<Outcome<InstalledPlugin>> InstallAsync(bool sign = true)
    {
        byte[] package = await TestPackages.FixtureAsync("fixture", Plugin, sign).ConfigureAwait(false);
        using MemoryStream stream = new(package);
        return await this.Runtime.InstallAsync(stream, TestPki.RepositoryId, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Closes the runtime and opens a fresh one, as a restart would.</summary>
    /// <returns>A task that completes when the new runtime is open.</returns>
    internal async Task ReopenAsync()
    {
        await this.Runtime.DisposeAsync().ConfigureAwait(false);
        this.Runtime = this.Open();
    }
}
