using System.Diagnostics;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Sandboxing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EMMA.Tests.PluginHost;

public sealed class PluginRuntimeRoutingTests
{
    [Fact]
    public async Task EnsureStartedAsync_ReturnsExternal_WhenEndpointExistsAndEntrypointMissing()
    {
        var options = Options.Create(new PluginHostOptions());
        var signatureOptions = Options.Create(new PluginSignatureOptions());
        var verifier = new HmacPluginSignatureVerifier(signatureOptions);
        var manager = new PluginProcessManager(
            options,
            new NoOpSandboxManager(),
            new ThrowingEntrypointResolver(),
            signatureOptions,
            verifier,
            NullLogger<PluginProcessManager>.Instance);

        var manifest = new PluginManifest(
            "demo",
            "Demo",
            "1.0.0",
            "grpc",
            "http://127.0.0.1:5005",
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        var status = await manager.EnsureStartedAsync(
            manifest,
            PluginRuntimeStatus.Unknown(),
            CancellationToken.None);

        Assert.Equal(PluginRuntimeState.External, status.State);
    }

    [Fact]
    public async Task EnsureStartedAsync_Disables_WhenMinHostVersionIsIncompatible()
    {
        var options = Options.Create(new PluginHostOptions());
        var signatureOptions = Options.Create(new PluginSignatureOptions());
        var verifier = new HmacPluginSignatureVerifier(signatureOptions);
        var manager = new PluginProcessManager(
            options,
            new NoOpSandboxManager(),
            new ThrowingEntrypointResolver(),
            signatureOptions,
            verifier,
            NullLogger<PluginProcessManager>.Instance);

        var manifest = new PluginManifest(
            "demo",
            "Demo",
            "1.0.0",
            "grpc",
            "http://127.0.0.1:5005",
            null,
            null,
            null,
            null,
            null,
            null,
            new PluginManifestRuntime("999.0.0"));

        var status = await manager.EnsureStartedAsync(
            manifest,
            PluginRuntimeStatus.Unknown(),
            CancellationToken.None);

        Assert.Equal(PluginRuntimeState.Disabled, status.State);
        Assert.Equal("host-version-incompatible", status.LastErrorCode);
    }

    [Fact]
    public async Task EnsureStartedAsync_Throws_WhenNoEndpointAndEntrypointMissing()
    {
        var options = Options.Create(new PluginHostOptions
        {
            EnableProcessPlugins = true
        });
        var signatureOptions = Options.Create(new PluginSignatureOptions());
        var verifier = new HmacPluginSignatureVerifier(signatureOptions);
        var manager = new PluginProcessManager(
            options,
            new NoOpSandboxManager(),
            new ThrowingEntrypointResolver(),
            signatureOptions,
            verifier,
            NullLogger<PluginProcessManager>.Instance);

        var manifest = new PluginManifest(
            "demo",
            "Demo",
            "1.0.0",
            "grpc",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureStartedAsync(
            manifest,
            PluginRuntimeStatus.Unknown(),
            CancellationToken.None));
    }

    [Fact]
    public async Task EnsureStartedAsync_ReturnsExternal_WhenWasmComponentIsInferred()
    {
        var sandboxRoot = Path.Combine(Path.GetTempPath(), "emma-plugin-tests", Guid.NewGuid().ToString("N"), "sandbox");
        var options = Options.Create(new PluginHostOptions
        {
            SandboxRootDirectory = sandboxRoot
        });
        var signatureOptions = Options.Create(new PluginSignatureOptions());
        var verifier = new HmacPluginSignatureVerifier(signatureOptions);
        var resolver = new PluginEntrypointResolver(options);
        var manager = new PluginProcessManager(
            options,
            new NoOpSandboxManager(),
            resolver,
            signatureOptions,
            verifier,
            NullLogger<PluginProcessManager>.Instance);

        var pluginRoot = Path.Combine(sandboxRoot, "demo");
        Directory.CreateDirectory(pluginRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(pluginRoot, "plugin.wasm"),
            [0x00, 0x61, 0x73, 0x6D, 0x0D, 0x00, 0x01, 0x00]);

        var manifest = new PluginManifest(
            "demo",
            "Demo",
            "1.0.0",
            "grpc",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        var status = await manager.EnsureStartedAsync(
            manifest,
            PluginRuntimeStatus.Unknown(),
            CancellationToken.None);

        Assert.Equal(PluginRuntimeState.External, status.State);
    }

    [Fact]
    public async Task EnsureStartedAsync_Disables_WhenWasmComponentDetectedAndWasmDisabled()
    {
        var sandboxRoot = Path.Combine(Path.GetTempPath(), "emma-plugin-tests", Guid.NewGuid().ToString("N"), "sandbox");
        var options = Options.Create(new PluginHostOptions
        {
            SandboxRootDirectory = sandboxRoot,
            EnableWasmPlugins = false
        });
        var signatureOptions = Options.Create(new PluginSignatureOptions());
        var verifier = new HmacPluginSignatureVerifier(signatureOptions);
        var resolver = new PluginEntrypointResolver(options);
        var manager = new PluginProcessManager(
            options,
            new NoOpSandboxManager(),
            resolver,
            signatureOptions,
            verifier,
            NullLogger<PluginProcessManager>.Instance);

        var pluginRoot = Path.Combine(sandboxRoot, "demo");
        Directory.CreateDirectory(pluginRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(pluginRoot, "plugin.wasm"),
            [0x00, 0x61, 0x73, 0x6D, 0x0D, 0x00, 0x01, 0x00]);

        var manifest = new PluginManifest(
            "demo",
            "Demo",
            "1.0.0",
            "grpc",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        var status = await manager.EnsureStartedAsync(
            manifest,
            PluginRuntimeStatus.Unknown(),
            CancellationToken.None);

        Assert.Equal(PluginRuntimeState.Disabled, status.State);
        Assert.Equal("wasm-plugins-disabled", status.LastErrorCode);
    }

    [Fact]
    public async Task EnsureStartedAsync_ReturnsExternal_WhenProcessDisabledAndEndpointPresent()
    {
        var options = Options.Create(new PluginHostOptions
        {
            EnableProcessPlugins = false,
            EnableExternalEndpointPlugins = true
        });
        var signatureOptions = Options.Create(new PluginSignatureOptions());
        var verifier = new HmacPluginSignatureVerifier(signatureOptions);
        var manager = new PluginProcessManager(
            options,
            new NoOpSandboxManager(),
            new ThrowingEntrypointResolver(),
            signatureOptions,
            verifier,
            NullLogger<PluginProcessManager>.Instance);

        var manifest = new PluginManifest(
            "demo",
            "Demo",
            "1.0.0",
            "grpc",
            "http://127.0.0.1:5005",
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        var status = await manager.EnsureStartedAsync(
            manifest,
            PluginRuntimeStatus.Unknown(),
            CancellationToken.None);

        Assert.Equal(PluginRuntimeState.External, status.State);
    }

    private sealed class ThrowingEntrypointResolver : IPluginEntrypointResolver
    {
        public string GetPluginRoot(string pluginId)
        {
            throw new InvalidOperationException("No plugin root available in test resolver.");
        }

        public string ResolveEntrypoint(PluginManifest manifest)
        {
            throw new InvalidOperationException("No entrypoint available in test resolver.");
        }

        public bool TryResolveWasmComponent(PluginManifest manifest, out string componentPath)
        {
            componentPath = string.Empty;
            return false;
        }
    }

    private sealed class NoOpSandboxManager : IPluginSandboxManager
    {
        public Task<PluginSandboxResult> PrepareAsync(PluginManifest manifest, CancellationToken cancellationToken)
        {
            return Task.FromResult(new PluginSandboxResult(string.Empty, false, false));
        }

        public ProcessStartInfo ApplyToStartInfo(PluginManifest manifest, ProcessStartInfo startInfo)
        {
            return startInfo;
        }

        public Task EnforceAsync(PluginManifest manifest, Process process, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
