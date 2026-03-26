using System.Diagnostics;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Sandboxing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EMMA.Tests.PluginHost;

public sealed class PluginSecurityTests
{
    [Fact]
    public async Task EnsureStartedAsync_DisablesUnsignedPlugin_WhenRequired()
    {
        var hostOptions = Options.Create(new PluginHostOptions
        {
            EnableProcessPlugins = true
        });
        var signatureOptions = Options.Create(new PluginSignatureOptions
        {
            RequireSignedPlugins = true,
            HmacKeyBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3 })
        });
        var verifier = new HmacPluginSignatureVerifier(signatureOptions);
        var resolver = new PluginEntrypointResolver(hostOptions);
        var manager = new PluginProcessManager(
            hostOptions,
            new NoOpSandboxManager(),
            resolver,
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
            null);

        var status = await manager.EnsureStartedAsync(
            manifest,
            PluginRuntimeStatus.Unknown(),
            CancellationToken.None);

        Assert.Equal(PluginRuntimeState.Disabled, status.State);
        Assert.Equal("signature-invalid", status.LastErrorCode);
    }

    [Fact]
    public async Task EnsureStartedAsync_RejectsMissingExecutable()
    {
        var sandboxRoot = Path.Combine(Path.GetTempPath(), "emma-plugin-tests", Guid.NewGuid().ToString("N"), "sandbox");
        var hostOptions = Options.Create(new PluginHostOptions
        {
            SandboxRootDirectory = sandboxRoot,
            EnableProcessPlugins = true
        });
        var signatureOptions = Options.Create(new PluginSignatureOptions());
        var verifier = new HmacPluginSignatureVerifier(signatureOptions);
        var resolver = new PluginEntrypointResolver(hostOptions);
        var manager = new PluginProcessManager(
            hostOptions,
            new NoOpSandboxManager(),
            resolver,
            signatureOptions,
            verifier,
            NullLogger<PluginProcessManager>.Instance);

        Directory.CreateDirectory(Path.Combine(sandboxRoot, "demo"));
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
            null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureStartedAsync(
            manifest,
            PluginRuntimeStatus.Unknown(),
            CancellationToken.None));
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
