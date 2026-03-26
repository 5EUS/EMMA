using System.Net;
using System.Net.Http;
using System.Text;
using EMMA.Contracts.Plugins;
using EMMA.Plugin.AspNetCore;
using EMMA.TestPlugin.Services;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace EMMA.Tests.PluginHost;

public sealed class PluginProviderTests
{
    private const string HostAuthHeader = "x-emma-plugin-host-auth";
    private const string HostAuthTokenEnvVar = "EMMA_PLUGIN_HOST_AUTH_TOKEN";
    private const string TestHostAuthToken = "provider-test-token";

    [Fact]
    public async Task SearchAndPageEndpoints_ReturnDemoData()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var previousToken = Environment.GetEnvironmentVariable(HostAuthTokenEnvVar);
        Environment.SetEnvironmentVariable(HostAuthTokenEnvVar, TestHostAuthToken);

        var app = BuildTestPluginServer();
        await app.StartAsync();

        try
        {
            var address = GetServerAddress(app);
            var channel = CreateChannel(address);
            var headers = CreateHeaders();

            var searchClient = new SearchProvider.SearchProviderClient(channel);
            var searchResponse = await searchClient.SearchAsync(new SearchRequest { Query = "demo" }, headers);

            Assert.True(searchResponse.Results.Count >= 2);
            Assert.Contains(searchResponse.Results, item => item.Id == "demo-1");

            var pageClient = new PageProvider.PageProviderClient(channel);
            var chapters = await pageClient.GetChaptersAsync(new ChaptersRequest { MediaId = "demo-1" }, headers);

            Assert.Single(chapters.Chapters);
            Assert.Equal("ch-1", chapters.Chapters[0].Id);

            var page = await pageClient.GetPageAsync(new PageRequest
            {
                MediaId = "demo-1",
                ChapterId = "ch-1",
                Index = 0
            }, headers);

            Assert.NotNull(page.Page);
            Assert.Equal("https://api.example.local/data/hash-1/page-1.jpg", page.Page.ContentUri);
        }
        finally
        {
            await app.StopAsync();
            Environment.SetEnvironmentVariable(HostAuthTokenEnvVar, previousToken);
        }
    }

    [Fact]
    public async Task VideoEndpoints_ReturnDemoStream()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var previousToken = Environment.GetEnvironmentVariable(HostAuthTokenEnvVar);
        Environment.SetEnvironmentVariable(HostAuthTokenEnvVar, TestHostAuthToken);

        var app = BuildTestPluginServer();
        await app.StartAsync();

        try
        {
            var address = GetServerAddress(app);
            var channel = CreateChannel(address);
            var headers = CreateHeaders();

            var client = new VideoProvider.VideoProviderClient(channel);
            var streams = await client.GetStreamsAsync(new StreamRequest { MediaId = "demo-video-1" }, headers);

            Assert.Single(streams.Streams);
            Assert.Equal("stream-1", streams.Streams[0].Id);

            var segment = await client.GetSegmentAsync(new SegmentRequest
            {
                MediaId = "demo-video-1",
                StreamId = "stream-1",
                Sequence = 0
            }, headers);

            Assert.NotEmpty(segment.Payload.ToByteArray());
            Assert.Equal("video/mp2t", segment.ContentType);
        }
        finally
        {
            await app.StopAsync();
            Environment.SetEnvironmentVariable(HostAuthTokenEnvVar, previousToken);
        }
    }

    private static Grpc.Core.Metadata CreateHeaders()
    {
        return new Grpc.Core.Metadata
        {
            { HostAuthHeader, TestHostAuthToken },
            { "x-correlation-id", Guid.NewGuid().ToString("n") }
        };
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
        builder.Services.AddScoped<ITestPluginRuntime, TestPluginRuntime>();
        builder.Services.Configure<PluginSdkControlOptions>(options =>
        {
            options.Message = "EMMA test plugin ready";
            options.CpuBudgetMs = 150;
            options.MemoryMb = 128;
            options.Capabilities.Add("test-plugin");
            options.Capabilities.Add("search");
            options.Capabilities.Add("pages");
            options.Capabilities.Add("video");
            options.Domains.Add("api.mangadex.org");
            options.Domains.Add("uploads.mangadex.org");
            options.Paths.Add("/plugin-data");
        });
        builder.Services.AddHttpClient<MangadexClient>(client =>
            {
                client.BaseAddress = new Uri("https://api.mangadex.org");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new FakeMangadexHandler());

        var app = builder.Build();
        app.MapGrpcService<PluginDefaultControlService>();
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

    private static GrpcChannel CreateChannel(string address)
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true
        };

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(address),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        return GrpcChannel.ForAddress(httpClient.BaseAddress!, new GrpcChannelOptions
        {
            HttpClient = httpClient
        });
    }

    private sealed class FakeMangadexHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }

            var path = request.RequestUri.AbsolutePath;
            if (path == "/manga")
            {
                return Task.FromResult(JsonResponse("""
                {
                  "data": [
                    { "id": "demo-1", "attributes": { "title": { "en": "Demo Paged Media" } } },
                    { "id": "demo-2", "attributes": { "title": { "en": "Demo Paged Media Two" } } }
                  ]
                }
                """));
            }

            if (path == "/manga/demo-1/feed")
            {
                return Task.FromResult(JsonResponse("""
                {
                  "data": [
                    { "id": "ch-1", "attributes": { "chapter": "1", "title": "Chapter One" } }
                  ]
                }
                """));
            }

            if (path == "/at-home/server/ch-1")
            {
                return Task.FromResult(JsonResponse("""
                {
                  "baseUrl": "https://api.example.local",
                  "chapter": { "hash": "hash-1", "data": ["page-1.jpg"] }
                }
                """));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
