using EMMA.Domain;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

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
            options,
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
            options,
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

        var pages = await host.GetPagesAsync(record, MediaId.Create("demo-1"), "ch-1", 0, 3, CancellationToken.None);
        Assert.Equal(3, pages.Pages.Count);
        Assert.False(pages.ReachedEnd);
        Assert.Equal(2, pages.Pages[2].Index);
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
            IReadOnlyList<string>? permittedDomains,
            CancellationToken cancellationToken)
        {
            if (operation == "handshake")
            {
                return Task.FromResult("{\"version\":\"0.1.0\",\"message\":\"ok\"}");
            }

            if (operation == "capabilities")
            {
                return Task.FromResult(
                    "[{\"name\":\"search\",\"mediaTypes\":[\"paged\"],\"operations\":[\"search\",\"chapters\",\"page\",\"pages\",\"invoke\"]}]");
            }

            if (operation == "chapters")
            {
                return Task.FromResult("[{\"id\":\"ch-1\",\"number\":1,\"title\":\"Chapter 1\",\"uploaderGroups\":[\"Team A\"]}]");
            }

            if (operation == "invoke")
            {
                var requestedOperation = operationArgs.Count > 0 ? operationArgs[0] : string.Empty;
                var argsJson = operationArgs.Count > 3 ? operationArgs[3] : string.Empty;

                return Task.FromResult(requestedOperation switch
                {
                    "search" => Success("[{\"id\":\"demo-1\",\"source\":\"demo\",\"title\":\"Demo Manga\",\"mediaType\":\"paged\"}]"),
                    "page" => BuildPageResult(argsJson),
                    "pages" => BuildPagesResult(argsJson),
                    _ => Error($"unsupported-operation:{requestedOperation}")
                });
            }

            throw new InvalidOperationException($"Unknown operation '{operation}'.");
        }

        private static string BuildPageResult(string argsJson)
        {
            var pageIndex = ReadInt(argsJson, "pageIndex", 0);
            return Success($"{{\"id\":\"p-{pageIndex + 1}\",\"index\":{pageIndex},\"contentUri\":\"https://example.com/p{pageIndex + 1}.jpg\"}}");
        }

        private static string BuildPagesResult(string argsJson)
        {
            var startIndex = ReadInt(argsJson, "startIndex", 0);
            var count = ReadInt(argsJson, "count", 1);
            var pages = Enumerable.Range(startIndex, count)
                .Select(index => $"{{\"id\":\"p-{index + 1}\",\"index\":{index},\"contentUri\":\"https://example.com/p{index + 1}.jpg\"}}")
                .ToArray();
            return Success($"[{string.Join(',', pages)}]");
        }

        private static int ReadInt(string json, string property, int fallback)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return fallback;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(property, out var value)
                    && value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt32(out var parsed))
                {
                    return parsed;
                }
            }
            catch
            {
            }

            return fallback;
        }

        private static string Success(string payloadJson)
            => $"{{\"isError\":false,\"error\":null,\"contentType\":\"application/json\",\"payloadJson\":{JsonSerializer.Serialize(payloadJson)}}}";

        private static string Error(string message)
            => $"{{\"isError\":true,\"error\":{JsonSerializer.Serialize(message)},\"contentType\":\"application/problem+json\",\"payloadJson\":\"\"}}";
    }
}
