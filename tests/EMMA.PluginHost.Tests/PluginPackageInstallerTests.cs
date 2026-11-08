using EMMA.Domain;
using EMMA.PluginHost.Install;
using EMMA.PluginHost.Manifest;
using EMMA.PluginHost.Signing;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.PluginHost.Tests;

/// <summary>Tests for <see cref="PluginPackageInstaller"/>: installing from an untrusted package.</summary>
public sealed class PluginPackageInstallerTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("emma-install-test-");
    private readonly FakeTimeProvider time = new(TestPki.Now);
    private readonly FakeDelegationSource source = new();
    private readonly FakeDelegationVersions versions = new();
    private readonly FakePluginRegistry registry = new();

    /// <summary>Initializes a new instance of the <see cref="PluginPackageInstallerTests"/> class.</summary>
    public PluginPackageInstallerTests() =>
        this.source.With(TestPki.RepositoryId, DelegationJson.Write(TestPki.Bundle()));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Removes the temporary directory.</summary>
    public void Dispose() => this.root.Delete(recursive: true);

    /// <summary>A verified package is installed: files on disk, an enabled registry record naming the signer, and no staging leftovers.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AVerifiedPackageIsInstalled()
    {
        byte[] component = TestPackages.Component();
        PluginManifest manifest = TestPackages.Manifest(component);

        Outcome<InstalledPlugin> result = await this.InstallAsync(TestPackages.Standard(manifest, component));

        Assert.True(result.IsSuccess, result.Error?.Message);
        string directory = Path.Combine(this.root.FullName, "com.example.test", "1.0.0");
        Assert.Equal(component, await File.ReadAllBytesAsync(Path.Combine(directory, "plugin.wasm"), Ct));
        Assert.True(File.Exists(Path.Combine(directory, "plugin.json")));
        Assert.Equal(PluginStatus.Enabled, result.Value.Status);
        Assert.Equal("test-repo/signer-1", result.Value.Signer);
        Assert.Equal(this.time.GetUtcNow(), result.Value.InstalledAt);
        Assert.Equal(result.Value, await this.registry.GetAsync(manifest.Id, Ct));
        Assert.Empty(this.StagingLeftovers());
    }

    /// <summary>A plugin whose entrypoint is in a subdirectory installs there.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ANestedEntrypointIsInstalled()
    {
        byte[] component = TestPackages.Component();
        PluginManifest manifest = TestPackages.Manifest(component, entrypointPath: "bin/plugin.wasm");

        Outcome<InstalledPlugin> result = await this.InstallAsync(TestPackages.Standard(manifest, component));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(File.Exists(Path.Combine(this.root.FullName, "com.example.test", "1.0.0", "bin", "plugin.wasm")));
    }

    /// <summary>An unsigned plugin is refused when the host requires signatures, and nothing is left behind.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AnUnsignedPluginIsRefusedWhenSignaturesAreRequired()
    {
        byte[] component = TestPackages.Component();

        Outcome<InstalledPlugin> result = await this.InstallAsync(TestPackages.Standard(TestPackages.Manifest(component, sign: false), component));

        Assert.Equal(ErrorCode.Unauthorized, result.Error?.Code);
        await this.AssertNothingInstalledAsync();
    }

    /// <summary>A development host may install an unsigned plugin, which is recorded as unsigned.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AnUnsignedPluginIsAllowedInDevelopment()
    {
        byte[] component = TestPackages.Component();

        Outcome<InstalledPlugin> result = await this.InstallAsync(
            TestPackages.Standard(TestPackages.Manifest(component, sign: false), component),
            policy: TestPki.Policy(requireSigned: false));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Null(result.Value.Signer);
    }

    /// <summary>A signature that is present must verify even when unsigned plugins are allowed, so development mode is not a way around a bad signature.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ABadSignatureIsRefusedEvenInDevelopment()
    {
        byte[] component = TestPackages.Component();
        PluginManifest forged = TestPki.Sign(TestPackages.Manifest(component, sign: false), key: TestPki.Attacker);

        Outcome<InstalledPlugin> result = await this.InstallAsync(TestPackages.Standard(forged, component), policy: TestPki.Policy(requireSigned: false));

        Assert.Equal(ErrorCode.Unauthorized, result.Error?.Code);
        await this.AssertNothingInstalledAsync();
    }

    /// <summary>Swapping the component after signing is caught by the entrypoint digest, and no files are installed.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ASubstitutedComponentIsRefused()
    {
        PluginManifest manifest = TestPackages.Manifest(TestPackages.Component("genuine"));

        Outcome<InstalledPlugin> result = await this.InstallAsync(TestPackages.Standard(manifest, TestPackages.Component("malicious")));

        Assert.Equal(ErrorCode.Unauthorized, result.Error?.Code);
        Assert.Contains("digest", result.Error?.Message, StringComparison.Ordinal);
        await this.AssertNothingInstalledAsync();
    }

    /// <summary>A package signed for one repository is refused when it arrives from another.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task APackageFromTheWrongRepositoryIsRefused()
    {
        byte[] component = TestPackages.Component();

        Outcome<InstalledPlugin> result = await this.InstallAsync(
            TestPackages.Standard(TestPackages.Manifest(component), component),
            repositoryId: "other-repo");

        Assert.Equal(ErrorCode.Unauthorized, result.Error?.Code);
        await this.AssertNothingInstalledAsync();
    }

    /// <summary>Hostile or malformed package structures are refused before anything is written.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task HostilePackageStructuresAreRefused()
    {
        byte[] component = TestPackages.Component();
        PluginManifest manifest = TestPackages.Manifest(component);
        byte[] manifestJson = ManifestWriter.Write(manifest);
        byte[][] hostile =
        [
            TestPackages.Zip(("plugin.json", manifestJson), ("plugin.wasm", component), ("../evil.wasm", component)),
            TestPackages.Zip(("plugin.json", manifestJson), ("plugin.wasm", component), ("/etc/cron.d/evil", component)),
            TestPackages.Zip(("plugin.json", manifestJson), ("plugin.wasm", component), ("..\\evil.wasm", component)),
            TestPackages.Zip(("plugin.json", manifestJson), ("plugin.wasm", component), ("a/../../evil.wasm", component)),
            TestPackages.Zip(("plugin.json", manifestJson), ("plugin.wasm", component), ("C:/evil.wasm", component)),
            TestPackages.Zip(("plugin.json", manifestJson), ("plugin.wasm", component), ("plugin.wasm", component)),
            TestPackages.Zip(("plugin.json", manifestJson), ("plugin.wasm", component), ("PLUGIN.WASM", component)),
            TestPackages.Zip(("plugin.json", manifestJson), ("plugin.wasm", component), ("extra.txt", component)),
            TestPackages.Zip(("plugin.wasm", component)),
            TestPackages.Zip(("plugin.json", manifestJson)),
            TestPackages.WithSymbolicLink(manifest),
            "this is not a zip file"u8.ToArray(),
        ];

        foreach (byte[] package in hostile)
        {
            Outcome<InstalledPlugin> result = await this.InstallAsync(package);

            Assert.Equal(ErrorCode.InvalidArgument, result.Error?.Code);
        }

        await this.AssertNothingInstalledAsync();
    }

    /// <summary>A sibling directory that merely shares a name prefix with the install directory is not a way out: traversal entries never reach the filesystem.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TraversalNeverEscapesTheInstallRoot()
    {
        byte[] component = TestPackages.Component();
        PluginManifest manifest = TestPackages.Manifest(component);
        string outside = this.root.FullName + "-evil";

        Outcome<InstalledPlugin> result = await this.InstallAsync(TestPackages.Zip(
            ("plugin.json", ManifestWriter.Write(manifest)),
            ("plugin.wasm", component),
            ($"../{Path.GetFileName(outside)}/evil.wasm", component)));

        Assert.False(result.IsSuccess);
        Assert.False(Directory.Exists(outside));
    }

    /// <summary>Package size limits are enforced: too many entries, and more bytes than allowed (counted as read).</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task PackageLimitsAreEnforced()
    {
        byte[] component = TestPackages.Component(new string('x', 5000));
        byte[] package = TestPackages.Standard(TestPackages.Manifest(component), component);

        Outcome<InstalledPlugin> tooMany = await this.InstallAsync(package, options: new PluginHostOptions(this.root.FullName, MaxPackageEntries: 1));
        Outcome<InstalledPlugin> tooBig = await this.InstallAsync(package, options: new PluginHostOptions(this.root.FullName, MaxUnpackedBytes: 1000));
        Outcome<InstalledPlugin> packageTooBig = await this.InstallAsync(package, options: new PluginHostOptions(this.root.FullName, MaxPackageBytes: 10));

        Assert.Equal(ErrorCode.InvalidArgument, tooMany.Error?.Code);
        Assert.Equal(ErrorCode.InvalidArgument, tooBig.Error?.Code);
        Assert.Equal(ErrorCode.InvalidArgument, packageTooBig.Error?.Code);
        await this.AssertNothingInstalledAsync();
    }

    /// <summary>A manifest that is invalid (here, with an extra property) is refused.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AnInvalidManifestIsRefused()
    {
        byte[] component = TestPackages.Component();
        PluginManifest manifest = TestPackages.Manifest(component);
        string json = System.Text.Encoding.UTF8.GetString(ManifestWriter.Write(manifest)).Replace("\"schema\": 1,", "\"schema\": 1, \"filesystem\": [\"/\"],", StringComparison.Ordinal);

        Outcome<InstalledPlugin> result = await this.InstallAsync(TestPackages.Zip(("plugin.json", System.Text.Encoding.UTF8.GetBytes(json)), ("plugin.wasm", component)));

        Assert.Equal(ErrorCode.InvalidArgument, result.Error?.Code);
        Assert.Contains("$.filesystem", result.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>An upgrade installs beside the old version and replaces the record; the same version and a downgrade are refused.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UpgradesWorkAndDowngradesAndRepeatsAreRefused()
    {
        byte[] component = TestPackages.Component();
        byte[] Package(string version) => TestPackages.Standard(TestPackages.Manifest(component, version: version), component);

        Assert.True((await this.InstallAsync(Package("1.0.0"))).IsSuccess);
        Assert.True((await this.InstallAsync(Package("1.1.0"))).IsSuccess);
        Outcome<InstalledPlugin> downgrade = await this.InstallAsync(Package("1.0.5"));
        Outcome<InstalledPlugin> repeat = await this.InstallAsync(Package("1.1.0"));

        Assert.Contains("downgrade", downgrade.Error?.Message, StringComparison.Ordinal);
        Assert.Contains("already installed", repeat.Error?.Message, StringComparison.Ordinal);
        Assert.Equal("1.1.0", (await this.registry.GetAsync(PluginId.Create("com.example.test"), Ct))!.Version);
        Assert.True(Directory.Exists(Path.Combine(this.root.FullName, "com.example.test", "1.0.0")));
        Assert.True(Directory.Exists(Path.Combine(this.root.FullName, "com.example.test", "1.1.0")));
    }

    /// <summary>If the registry write fails the moved files are taken back out, so nothing is half-installed.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ARegistryFailureRollsTheFilesBack()
    {
        byte[] component = TestPackages.Component();
        this.registry.FailOnUpsert = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => this.InstallAsync(TestPackages.Standard(TestPackages.Manifest(component), component)));

        await this.AssertNothingInstalledAsync();
    }

    /// <summary>Two installs of the same version racing leave exactly one winner and no exception.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RacingInstallsOfTheSameVersionHaveOneWinner()
    {
        byte[] component = TestPackages.Component();
        byte[] package = TestPackages.Standard(TestPackages.Manifest(component), component);

        Outcome<InstalledPlugin>[] results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => this.InstallAsync(package)));

        Assert.Equal(1, results.Count(result => result.IsSuccess));
        Assert.Empty(this.StagingLeftovers());
    }

    /// <summary>Version comparison follows SemVer precedence, including pre-releases.</summary>
    [Fact]
    public void VersionsCompareBySemVerPrecedence()
    {
        string[] ascending = ["0.9.9", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0", "10.0.0"];

        for (int index = 0; index < ascending.Length - 1; index++)
        {
            Assert.True(PluginVersion.Compare(ascending[index], ascending[index + 1]) < 0, $"{ascending[index]} < {ascending[index + 1]}");
            Assert.True(PluginVersion.Compare(ascending[index + 1], ascending[index]) > 0);
        }

        Assert.Equal(0, PluginVersion.Compare("1.2.3", "1.2.3"));
    }

    private IReadOnlyList<string> StagingLeftovers()
    {
        string staging = Path.Combine(this.root.FullName, ".staging");
        return Directory.Exists(staging) ? [.. Directory.GetFileSystemEntries(staging)] : [];
    }

    private async Task AssertNothingInstalledAsync()
    {
        Assert.Empty(await this.registry.ListAsync(Ct).ConfigureAwait(false));
        Assert.DoesNotContain(this.root.GetDirectories(), directory => !string.Equals(directory.Name, ".staging", StringComparison.Ordinal));
        Assert.Empty(this.StagingLeftovers());
    }

    private async Task<Outcome<InstalledPlugin>> InstallAsync(
        byte[] package,
        string repositoryId = TestPki.RepositoryId,
        PluginHostOptions? options = null,
        TrustPolicy? policy = null)
    {
        TrustPolicy trust = policy ?? TestPki.Policy();
        PluginSignatureVerifier verifier = new(trust, this.source, this.versions, this.time);
        PluginPackageInstaller installer = new(options ?? new PluginHostOptions(this.root.FullName), trust, verifier, this.registry, this.time);
        using MemoryStream stream = new(package);
        return await installer.InstallAsync(stream, repositoryId, Ct).ConfigureAwait(false);
    }
}
