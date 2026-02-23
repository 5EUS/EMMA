using System.Net;
using System.Text.Json;
using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EMMA.Tests.PluginHost;

public sealed class PagedPipelineIntegrationTests
{
    [Fact]
    public async Task PipelineFlow_UsesCacheForSearchAndChapters()
    {
        await using var harness = await PipelineHarness.CreateAsync();

        var searchResponse = await harness.Client.GetAsync("/pipeline/paged/search?query=demo&pluginId=demo");
        searchResponse.EnsureSuccessStatusCode();

        var chaptersResponse = await harness.Client.GetAsync("/pipeline/paged/chapters?mediaId=demo-1&pluginId=demo");
        chaptersResponse.EnsureSuccessStatusCode();

        var pageResponse = await harness.Client.GetAsync(
            "/pipeline/paged/page?mediaId=demo-1&chapterId=ch-1&index=0&pluginId=demo");
        pageResponse.EnsureSuccessStatusCode();

        var searchResponse2 = await harness.Client.GetAsync("/pipeline/paged/search?query=demo&pluginId=demo");
        searchResponse2.EnsureSuccessStatusCode();

        var chaptersResponse2 = await harness.Client.GetAsync("/pipeline/paged/chapters?mediaId=demo-1&pluginId=demo");
        chaptersResponse2.EnsureSuccessStatusCode();

        Assert.Equal(1, harness.Counters.SearchCalls);
        Assert.Equal(1, harness.Counters.ChapterCalls);
        Assert.Equal(1, harness.Counters.PageCalls);
    }

    private sealed class PipelineHarness : IAsyncDisposable
    {
        private readonly WebApplicationFactory<global::Program> _factory;

        private PipelineHarness(
            WebApplicationFactory<global::Program> factory,
            HttpClient client,
            WebApplication pluginApp,
            string tempRoot,
            CallCounters counters)
        {
            _factory = factory;
            Client = client;
            PluginApp = pluginApp;
            TempRoot = tempRoot;
            Counters = counters;
        }

        public HttpClient Client { get; }
        public WebApplication PluginApp { get; }
        public string TempRoot { get; }
        public CallCounters Counters { get; }

        public static async Task<PipelineHarness> CreateAsync()
        {
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            var counters = new CallCounters();
            var tempRoot = Path.Combine(Path.GetTempPath(), "emma-pipeline-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            var pluginApp = BuildPluginServer(counters);
            await pluginApp.StartAsync();

            var address = GetServerAddress(pluginApp);
            var manifestPath = Path.Combine(tempRoot, "demo.plugin.json");
            await File.WriteAllTextAsync(manifestPath, $"{{\n  \"id\": \"demo\",\n  \"name\": \"Demo Plugin\",\n  \"version\": \"1.0.0\",\n  \"entry\": {{\n    \"protocol\": \"grpc\",\n    \"endpoint\": \"{address}\"\n  }},\n  \"capabilities\": {{\n    \"network\": [\"http\"],\n    \"cache\": true\n  }}\n}}\n");

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

                        config.AddInMemoryCollection(settings);
                    });
                });

            var client = factory.CreateClient();
            var refresh = await client.PostAsync("/plugins/refresh", content: null);
            refresh.EnsureSuccessStatusCode();

            return new PipelineHarness(factory, client, pluginApp, tempRoot, counters);
        }

        public async ValueTask DisposeAsync()
        {
            await PluginApp.StopAsync();
            _factory.Dispose();
            TryDelete(TempRoot);
        }
    }

    private sealed class CallCounters
    {
        private int _searchCalls;
        private int _chapterCalls;
        private int _pageCalls;

        public int SearchCalls => _searchCalls;
        public int ChapterCalls => _chapterCalls;
        public int PageCalls => _pageCalls;

        public void IncrementSearch() => Interlocked.Increment(ref _searchCalls);
        public void IncrementChapters() => Interlocked.Increment(ref _chapterCalls);
        public void IncrementPage() => Interlocked.Increment(ref _pageCalls);
    }

    private sealed class CountingSearchProvider(CallCounters counters) : SearchProvider.SearchProviderBase
    {
        private readonly CallCounters _counters = counters;

        public override Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
        {
            _counters.IncrementSearch();
            var response = new SearchResponse();
            response.Results.Add(new MediaSummary
            {
                Id = "demo-1",
                Source = "test",
                Title = "Demo",
                MediaType = "paged"
            });
            return Task.FromResult(response);
        }
    }

    private sealed class CountingPageProvider(CallCounters counters) : PageProvider.PageProviderBase
    {
        private readonly CallCounters _counters = counters;

        public override Task<ChaptersResponse> GetChapters(ChaptersRequest request, ServerCallContext context)
        {
            _counters.IncrementChapters();
            var response = new ChaptersResponse();
            response.Chapters.Add(new MediaChapter
            {
                Id = "ch-1",
                Number = 1,
                Title = "Chapter One"
            });
            return Task.FromResult(response);
        }

        public override Task<PageResponse> GetPage(PageRequest request, ServerCallContext context)
        {
            _counters.IncrementPage();
            var response = new PageResponse
            {
                Page = new MediaPage
                {
                    Id = "page-1",
                    Index = 0,
                    ContentUri = "https://example.invalid/demo-1/page-1.jpg"
                }
            };
            return Task.FromResult(response);
        }
    }

    private static WebApplication BuildPluginServer(CallCounters counters)
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
        builder.Services.AddSingleton(counters);
        builder.Services.AddSingleton<CountingSearchProvider>();
        builder.Services.AddSingleton<CountingPageProvider>();

        var app = builder.Build();
        app.MapGrpcService<CountingSearchProvider>();
        app.MapGrpcService<CountingPageProvider>();

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
