using EMMA.Domain;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EMMA.Tests.PluginHost;

public sealed class WasmPluginRuntimeHostTests
{
    [Fact]
    public async Task HandshakeAsync_Succeeds_WhenComponentInvokerReturnsValidJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "emma-wasm-tests", Guid.NewGuid().ToString("N"));
        var sandboxRoot = Path.Combine(root, "sandbox");

        var options = Options.Create(new PluginHostOptions
        {
            SandboxRootDirectory = sandboxRoot
        });

        var resolver = new PluginEntrypointResolver(options);
        var host = new WasmPluginRuntimeHost(
            resolver,
            new FakeWasmComponentInvoker(),
            NullLogger<WasmPluginRuntimeHost>.Instance);

        var pluginRoot = Path.Combine(sandboxRoot, "demo");
        Directory.CreateDirectory(pluginRoot);

        var componentPath = Path.Combine(pluginRoot, "plugin.wasm");
        await WriteWasmComponentHeaderAsync(componentPath);

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
            new PluginManifestRuntime("1.0.0"));

        var handshake = await host.HandshakeAsync(manifest, CancellationToken.None);
        Assert.True(handshake.Success);
        Assert.Equal("0.1.0", handshake.Version);
        Assert.Contains("search", handshake.Capabilities);
    }

    [Fact]
    public async Task SearchAndPaging_Work_WhenComponentInvokerReturnsValidJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "emma-wasm-tests", Guid.NewGuid().ToString("N"));
        var sandboxRoot = Path.Combine(root, "sandbox");

        var options = Options.Create(new PluginHostOptions
        {
            SandboxRootDirectory = sandboxRoot
        });

        var resolver = new PluginEntrypointResolver(options);
        var host = new WasmPluginRuntimeHost(
            resolver,
            new FakeWasmComponentInvoker(),
            NullLogger<WasmPluginRuntimeHost>.Instance);

        var pluginRoot = Path.Combine(sandboxRoot, "demo");
        Directory.CreateDirectory(pluginRoot);

        var componentPath = Path.Combine(pluginRoot, "plugin.wasm");
        await WriteWasmComponentHeaderAsync(componentPath);

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
            new PluginManifestRuntime("1.0.0"));

        var record = new PluginRecord(manifest, PluginHandshakeDefaults.NotChecked(), PluginRuntimeStatus.External());
        var search = await host.SearchAsync(record, "Manga", CancellationToken.None);
        Assert.Single(search);
        Assert.Equal("demo-1", search[0].Id.Value);

        var chapters = await host.GetChaptersAsync(record, MediaId.Create("demo-1"), CancellationToken.None);
        Assert.Single(chapters);
        Assert.Equal("ch-1", chapters[0].ChapterId);

        var page = await host.GetPageAsync(record, MediaId.Create("demo-1"), "ch-1", 0, CancellationToken.None);
        Assert.Equal("p-1", page.PageId);
    }

    private static Task WriteWasmComponentHeaderAsync(string wasmPath)
    {
        return File.WriteAllBytesAsync(wasmPath, [0x00, 0x61, 0x73, 0x6D, 0x0D, 0x00, 0x01, 0x00]);
    }

    private sealed class FakeWasmComponentInvoker : IWasmComponentInvoker
    {
        public Task<string> InvokeAsync(
            string componentPath,
            string operation,
            IReadOnlyList<string> operationArgs,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(operation switch
            {
                "handshake" => "{\"version\":\"0.1.0\",\"message\":\"ok\"}",
                "capabilities" => "[\"health\",\"search\",\"paged\"]",
                "search" => "[{\"id\":\"demo-1\",\"source\":\"demo\",\"title\":\"Demo Manga\",\"mediaType\":\"paged\"}]",
                "chapters" => "[{\"id\":\"ch-1\",\"number\":1,\"title\":\"Chapter 1\"}]",
                "page" => "{\"id\":\"p-1\",\"index\":0,\"contentUri\":\"https://example.com/p1.jpg\"}",
                _ => throw new InvalidOperationException($"Unknown operation '{operation}'.")
            });
        }
    }
}
