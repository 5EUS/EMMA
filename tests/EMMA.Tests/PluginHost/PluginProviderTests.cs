using System.Net;
using System.Net.Http;
using EMMA.Contracts.Plugins;
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
        builder.Services.AddSingleton(new PluginFixture("https://api.example.local/data/hash-1/page-1.jpg"));

        var app = builder.Build();
        app.MapGrpcService<PluginControlService>();
        app.MapGrpcService<SearchProviderService>();
        app.MapGrpcService<PageProviderService>();
        app.MapGrpcService<VideoProviderService>();

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

    private sealed record PluginFixture(string AssetUrl);

    private sealed class PluginControlService : PluginControl.PluginControlBase
    {
        public override Task<HealthResponse> GetHealth(HealthRequest request, Grpc.Core.ServerCallContext context)
        {
            return Task.FromResult(new HealthResponse
            {
                Status = "ok",
                Version = "1.0.0",
                Message = "ok"
            });
        }

        public override Task<CapabilitiesResponse> GetCapabilities(CapabilitiesRequest request, Grpc.Core.ServerCallContext context)
        {
            var response = new CapabilitiesResponse
            {
                Budgets = new CapabilityBudgets { CpuBudgetMs = 150, MemoryMb = 128 },
                Permissions = new CapabilityPermissions()
            };
            response.Capabilities.Add("health");
            response.Capabilities.Add("capabilities");
            response.Capabilities.Add("search");
            response.Capabilities.Add("pages");
            response.Capabilities.Add("video");
            return Task.FromResult(response);
        }
    }

    private sealed class SearchProviderService : SearchProvider.SearchProviderBase
    {
        public override Task<SearchResponse> Search(SearchRequest request, Grpc.Core.ServerCallContext context)
        {
            var response = new SearchResponse();
            response.Results.Add(new MediaSummary
            {
                Id = "demo-1",
                Source = "demo",
                Title = "Demo Paged Media",
                MediaType = "paged"
            });
            response.Results.Add(new MediaSummary
            {
                Id = "demo-2",
                Source = "demo",
                Title = "Demo Paged Media Two",
                MediaType = "paged"
            });
            return Task.FromResult(response);
        }
    }

    private sealed class PageProviderService(PluginFixture fixture) : PageProvider.PageProviderBase
    {
        private readonly PluginFixture _fixture = fixture;

        public override Task<ChaptersResponse> GetChapters(ChaptersRequest request, Grpc.Core.ServerCallContext context)
        {
            var response = new ChaptersResponse();
            response.Chapters.Add(new MediaChapter
            {
                Id = "ch-1",
                Number = 1,
                Title = "Chapter One"
            });
            return Task.FromResult(response);
        }

        public override Task<PageResponse> GetPage(PageRequest request, Grpc.Core.ServerCallContext context)
        {
            var response = new PageResponse
            {
                Page = new MediaPage
                {
                    Id = "page-1",
                    Index = request.Index,
                    ContentUri = _fixture.AssetUrl
                }
            };

            return Task.FromResult(response);
        }
    }

    private sealed class VideoProviderService : VideoProvider.VideoProviderBase
    {
        public override Task<StreamResponse> GetStreams(StreamRequest request, Grpc.Core.ServerCallContext context)
        {
            var response = new StreamResponse();
            response.Streams.Add(new StreamInfo
            {
                Id = "stream-1",
                Label = "Main",
                PlaylistUri = "https://api.example.local/video/master.m3u8"
            });
            return Task.FromResult(response);
        }

        public override Task<SegmentResponse> GetSegment(SegmentRequest request, Grpc.Core.ServerCallContext context)
        {
            return Task.FromResult(new SegmentResponse
            {
                ContentType = "video/mp2t",
                Payload = Google.Protobuf.ByteString.CopyFrom(new byte[] { 1, 2, 3, 4 })
            });
        }
    }
}
