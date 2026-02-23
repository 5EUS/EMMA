using System.Net;
using System.Text.Json;
using EMMA.TestPlugin;
using EMMA.TestPlugin.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EMMA.Tests.PluginHost;

public sealed class ProbeEndpointTests
{
    [Fact]
    public async Task ProbeRespectsTimeouts()
    {
        await using var harness = await ProbeHarness.CreateAsync(additionalSettings: new Dictionary<string, string?>
        {
            ["PluginHost:ProbeTimeoutSeconds"] = "1"
        });

        var response = await harness.Client.GetAsync("/probe/search?query=demo&pluginId=demo");
        response.EnsureSuccessStatusCode();
    }
    [Fact]
    public async Task ProbeSearch_ReturnsResults()
    {
        await using var harness = await ProbeHarness.CreateAsync();

        var response = await harness.Client.GetAsync("/probe/search?query=demo&pluginId=demo");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.True(root.GetProperty("count").GetInt32() > 0);
        Assert.Equal(
            TestPluginData.DemoMediaId,
            root.GetProperty("results")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task ProbePages_ReturnsChaptersAndPage()
    {
        await using var harness = await ProbeHarness.CreateAsync();

        var response = await harness.Client.GetAsync(
            $"/probe/pages?mediaId={TestPluginData.DemoMediaId}&chapterId={TestPluginData.DemoChapterId}&index=0&pluginId=demo");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("chapters").GetArrayLength());
        Assert.Equal(
            TestPluginData.Page.ContentUri,
            root.GetProperty("page").GetProperty("contentUri").GetString());
    }

    [Fact]
    public async Task ProbeVideo_ReturnsStreamsAndSegment()
    {
        await using var harness = await ProbeHarness.CreateAsync();

        var response = await harness.Client.GetAsync(
            $"/probe/video?mediaId={TestPluginData.DemoVideoId}&streamId={TestPluginData.DemoStreamId}&sequence=0&pluginId=demo");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("streams").GetArrayLength());
        Assert.True(root.GetProperty("segment").GetProperty("size").GetInt32() > 0);
    }

    [Fact]
    public async Task ProbePipeline_ReturnsComposite()
    {
        await using var harness = await ProbeHarness.CreateAsync();

        var response = await harness.Client.GetAsync("/probe/pipeline?query=demo&pluginId=demo");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.True(root.GetProperty("searchCount").GetInt32() > 0);
        Assert.Equal(
            TestPluginData.DemoMediaId,
            root.GetProperty("selected").GetProperty("id").GetString());
        Assert.Equal(
            TestPluginData.Page.ContentUri,
            root.GetProperty("page").GetProperty("contentUri").GetString());
    }

    private sealed class ProbeHarness : IAsyncDisposable
    {
        private readonly WebApplicationFactory<global::Program> _factory;

        private ProbeHarness(
            WebApplicationFactory<global::Program> factory,
            HttpClient client,
            WebApplication pluginApp,
            string tempRoot)
        {
            _factory = factory;
            Client = client;
            PluginApp = pluginApp;
            TempRoot = tempRoot;
        }

        public HttpClient Client { get; }
        public WebApplication PluginApp { get; }
        public string TempRoot { get; }

        public static async Task<ProbeHarness> CreateAsync(Dictionary<string, string?>? additionalSettings = null)
        {
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            var tempRoot = Path.Combine(Path.GetTempPath(), "emma-probe-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            var pluginApp = BuildTestPluginServer();
            await pluginApp.StartAsync();

            var address = GetServerAddress(pluginApp);
            var manifestPath = Path.Combine(tempRoot, "demo.plugin.json");
            await File.WriteAllTextAsync(manifestPath, $"{{\n  \"id\": \"demo\",\n  \"name\": \"Demo Plugin\",\n  \"version\": \"1.0.0\",\n  \"entry\": {{\n    \"protocol\": \"grpc\",\n    \"endpoint\": \"{address}\"\n  }},\n  \"capabilities\": {{\n    \"network\": [\"http\", \"https\"],\n    \"cache\": true,\n    \"fileSystem\": [\"read\"]\n  }}\n}}\n");

            var factory = new WebApplicationFactory<global::Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, config) =>
                    {
                        var settings = new Dictionary<string, string?>
                        {
                            ["PluginHost:ManifestDirectory"] = tempRoot,
                            ["PluginHost:HandshakeOnStartup"] = "false",
                            ["PluginHost:HandshakeTimeoutSeconds"] = "5"
                        };

                        if (additionalSettings is not null)
                        {
                            foreach (var entry in additionalSettings)
                            {
                                settings[entry.Key] = entry.Value;
                            }
                        }

                        config.AddInMemoryCollection(settings);
                    });
                });

            var client = factory.CreateClient();
            var refresh = await client.PostAsync("/plugins/refresh", content: null);
            refresh.EnsureSuccessStatusCode();

            return new ProbeHarness(factory, client, pluginApp, tempRoot);
        }

        public async ValueTask DisposeAsync()
        {
            await PluginApp.StopAsync();
            _factory.Dispose();
            TryDelete(TempRoot);
        }
    }

    private static WebApplication BuildTestPluginServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
            });
        });

        builder.Services.AddGrpc();

        var app = builder.Build();
        app.MapGrpcService<TestPluginControlService>();
        app.MapGrpcService<TestSearchProviderService>();
        app.MapGrpcService<TestPageProviderService>();
        app.MapGrpcService<TestVideoProviderService>();

        return app;
    }

    private static string GetServerAddress(IApplicationBuilder app)
    {
        var server = app.ApplicationServices.GetRequiredService<IServer>();
        var addressFeature = server.Features.Get<IServerAddressesFeature>();
        var address = addressFeature?.Addresses.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(address))
        {
            throw new InvalidOperationException("Failed to determine plugin server address.");
        }

        return address;
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
        }
    }
}
