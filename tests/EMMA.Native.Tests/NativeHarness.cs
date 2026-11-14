using System.Text;
using System.Text.Json.Nodes;

using EMMA.PluginHost.Signing;
using EMMA.TestSupport;

using Microsoft.Data.Sqlite;

namespace EMMA.Native.Tests;

/// <summary>
/// An open library handle over a temporary data directory that trusts the test repository. Calls go through
/// <see cref="NativeApi"/>, the same code the C exports call, with real JSON in and out.
/// </summary>
internal sealed class NativeHarness : IAsyncDisposable
{
    /// <summary>The id the fixture plugin is installed under.</summary>
    internal const string Plugin = "com.example.fixture";

    private readonly DirectoryInfo root;

    private NativeHarness(DirectoryInfo root, nint handle)
    {
        this.root = root;
        this.Handle = handle;
    }

    /// <summary>Gets the handle.</summary>
    internal nint Handle { get; }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        NativeApi.Close(this.Handle);
        SqliteConnection.ClearAllPools();
        this.root.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>Creates a data directory trusting the test repository (with a delegation valid now) and opens a handle.</summary>
    /// <returns>The harness.</returns>
    internal static async Task<NativeHarness> StartAsync()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("emma-native-test-");
        string delegations = Path.Combine(directory.FullName, "delegations");
        Directory.CreateDirectory(delegations);
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DelegatedSigner signer = TestPki.Delegate(validFrom: now.AddDays(-30), validUntil: now.AddDays(30));
        await File.WriteAllBytesAsync(Path.Combine(delegations, $"{TestPki.RepositoryId}.delegations.json"), DelegationJson.Write(TestPki.Bundle(1, signer))).ConfigureAwait(false);

        JsonObject config = new()
        {
            ["data_dir"] = directory.FullName,
            ["user"] = "tester",
            ["trust"] = new JsonObject
            {
                ["repositories"] = new JsonArray(new JsonObject
                {
                    ["id"] = TestPki.RepositoryId,
                    ["root_keys"] = new JsonObject { [TestPki.RootKeyId] = TestPki.Root.PublicPem },
                }),
            },
        };
        int status = NativeApi.Create(Encoding.UTF8.GetBytes(config.ToJsonString()), out nint handle, out NativeResponse? error);
        return status == NativeStatus.Ok
            ? new NativeHarness(directory, handle)
            : throw new InvalidOperationException($"The library did not start: {Encoding.UTF8.GetString(error?.Json ?? [])}");
    }

    /// <summary>Installs the fixture plugin from a signed package file.</summary>
    /// <param name="networkHosts">The hosts the plugin may reach; none by default.</param>
    /// <returns>The call result.</returns>
    internal async Task<CallResult> InstallFixtureAsync(params string[] networkHosts)
    {
        byte[] package = await TestPackages.FixtureAsync(
            "fixture",
            Plugin,
            adjust: manifest => manifest with { Permissions = manifest.Permissions with { Network = networkHosts } },
            signedAt: TimeProvider.System.GetUtcNow()).ConfigureAwait(false);
        string path = Path.Combine(this.root.FullName, "fixture.zip");
        await File.WriteAllBytesAsync(path, package).ConfigureAwait(false);
        return this.Call("plugins.install", new JsonObject { ["package_path"] = path, ["repository"] = TestPki.RepositoryId });
    }

    /// <summary>Runs an operation.</summary>
    /// <param name="operation">The operation name.</param>
    /// <param name="request">The request, or <see langword="null"/> for none.</param>
    /// <returns>The status and parsed envelope.</returns>
    internal CallResult Call(string operation, JsonNode? request = null)
    {
        byte[] body = request is null ? [] : Encoding.UTF8.GetBytes(request.ToJsonString());
        (int status, NativeResponse response) = NativeApi.Invoke(this.Handle, operation, body);
        return new CallResult(status, JsonNode.Parse(response.Json)!, response.Blob);
    }
}
