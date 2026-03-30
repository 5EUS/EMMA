using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Sandboxing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EMMA.Tests.PluginHost;

public sealed class LinuxPluginSandboxManagerTests
{
    [Fact]
    public async Task PrepareAsync_Throws_WhenSandboxEnabledAndBwrapMissingWithoutFallback()
    {
        var options = Options.Create(new PluginHostOptions
        {
            SandboxEnabled = true,
            AllowNoSandboxFallback = false,
            SandboxRootDirectory = Path.Combine(Path.GetTempPath(), "emma-sandbox-tests", Guid.NewGuid().ToString("N"))
        });

        var manager = new LinuxPluginSandboxManager(options, NullLogger<LinuxPluginSandboxManager>.Instance);

        var manifest = new PluginManifest(
            Id: "demo",
            Name: "Demo",
            Version: "1.0.0",
            Protocol: "grpc",
            Endpoint: "http://127.0.0.1:5005",
            MediaTypes: ["paged"],
            Capabilities: null,
            Permissions: null,
            Signature: null,
            Description: null,
            Author: null);

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", string.Empty);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.PrepareAsync(manifest, CancellationToken.None));
            Assert.Contains("bubblewrap", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }
}
