using EMMA.Domain;
using EMMA.PluginHost.Install;
using EMMA.PluginHost.Manifest;
using EMMA.PluginHost.Runtime;
using EMMA.PluginHost.Signing;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

namespace EMMA.WasmRuntime.Tests;

/// <summary>A complete host over the real native runtime: installer, loader and gateway with fakes for storage.</summary>
internal sealed class GatewayHarness : IDisposable
{
    /// <summary>The id every harness plugin is installed under.</summary>
    internal static readonly PluginId Id = PluginId.Create("com.example.fixture");

    private readonly FakeDelegationSource source = new();
    private readonly FakeDelegationVersions versions = new();
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("emma-gateway-test-");
    private readonly PluginHostOptions options;

    /// <summary>Initializes a new instance of the <see cref="GatewayHarness"/> class.</summary>
    /// <param name="runtime">The native runtime.</param>
    /// <param name="requireSigned">Whether unsigned plugins are refused.</param>
    /// <param name="maxTraps">How many consecutive traps quarantine a plugin.</param>
    internal GatewayHarness(WasmRuntimeHost runtime, bool requireSigned = true, int maxTraps = 5)
    {
        TrustPolicy policy = TestPki.Policy(requireSigned);
        this.options = new PluginHostOptions(this.root.FullName);
        this.source.With(TestPki.RepositoryId, DelegationJson.Write(TestPki.Bundle()));
        PluginSignatureVerifier verifier = new(policy, this.source, this.versions, this.Time);
        this.Installer = new PluginPackageInstaller(this.options, policy, verifier, this.Registry, this.Time);
        PluginLoader loader = new(this.options, policy, verifier, this.Registry, this.Settings, runtime, this.Time);
        this.Gateway = new WasmPluginGateway(loader, this.Registry, this.Log, this.Time, new WasmGatewayOptions(maxTraps));
    }

    /// <summary>Gets the test clock.</summary>
    internal FakeTimeProvider Time { get; } = new(TestPki.Now);

    /// <summary>Gets the installed-plugin registry.</summary>
    internal FakePluginRegistry Registry { get; } = new();

    /// <summary>Gets the user settings.</summary>
    internal FakePluginSettings Settings { get; } = new();

    /// <summary>Gets what plugins logged.</summary>
    internal FakePluginLog Log { get; } = new();

    /// <summary>Gets the installer.</summary>
    internal PluginPackageInstaller Installer { get; }

    /// <summary>Gets the gateway under test.</summary>
    internal WasmPluginGateway Gateway { get; }

    /// <summary>Gets where the installed component lives on disk.</summary>
    internal string InstalledEntrypoint => Path.Combine(this.options.PluginDirectory(Id, "1.0.0"), "plugin.wasm");

    /// <inheritdoc/>
    public void Dispose()
    {
        this.Gateway.Dispose();
        this.root.Delete(recursive: true);
    }

    /// <summary>Builds a signed (or unsigned) package around a fixture component.</summary>
    /// <param name="fixture">The fixture name.</param>
    /// <param name="sign">Whether to sign it.</param>
    /// <param name="adjust">Changes the manifest before signing.</param>
    /// <returns>The zip.</returns>
    internal static async Task<byte[]> PackageAsync(string fixture, bool sign = true, Func<PluginManifest, PluginManifest>? adjust = null)
    {
        byte[] component = await File.ReadAllBytesAsync(NativeArtifacts.Plugin(fixture)).ConfigureAwait(false);
        PluginManifest manifest = TestPackages.Manifest(component, Id.Value, sign: false);
        manifest = manifest with { Kinds = [], Permissions = manifest.Permissions with { Network = [] } };
        manifest = adjust?.Invoke(manifest) ?? manifest;
        return TestPackages.Standard(sign ? TestPki.Sign(manifest) : manifest, component);
    }

    /// <summary>Installs a package.</summary>
    /// <param name="package">The zip.</param>
    /// <param name="cancellationToken">Cancels the install.</param>
    /// <returns>The install result.</returns>
    internal async Task<Outcome<InstalledPlugin>> InstallAsync(byte[] package, CancellationToken cancellationToken)
    {
        using MemoryStream stream = new(package);
        return await this.Installer.InstallAsync(stream, TestPki.RepositoryId, cancellationToken).ConfigureAwait(false);
    }
}
