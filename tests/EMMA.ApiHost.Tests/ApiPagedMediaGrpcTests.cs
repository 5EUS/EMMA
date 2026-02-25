using System.Net;
using EMMA.Contracts.Api.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EMMA.ApiHost.Tests;

public sealed class ApiPagedMediaGrpcTests
{
    [Fact]
    public async Task Search_ReturnsPagedResults()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        await using var pipelineServer = await PipelineServer.StartAsync();

        await using var apiHostFactory = new WebApplicationFactory<EMMA.ApiHost.ApiHostEntryPoint>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    var settings = new Dictionary<string, string?>
                    {
                        ["PluginHost:BaseUrl"] = pipelineServer.Address,
                        ["PluginHost:PluginId"] = "demo",
                        ["ApiAuth:Enabled"] = "true",
                        ["ApiAuth:Keys:0:Key"] = "test-key",
                        ["ApiAuth:Keys:0:ClientId"] = "test-client"
                    };

                    config.AddInMemoryCollection(settings);
                });
            });

        var httpClient = apiHostFactory.CreateClient();
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpClient = httpClient
        });

        var client = new PagedMediaApi.PagedMediaApiClient(channel);
        var headers = new Metadata
        {
            { "x-api-key", "test-key" }
        };
        var response = await client.SearchAsync(new SearchRequest
        {
            Query = "demo",
            Context = new ApiRequestContext
            {
                CorrelationId = "test",
                DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O"),
                ClientId = "test-client"
            }
        }, headers);

        Assert.Equal(SearchResponse.OutcomeOneofCase.Result, response.OutcomeCase);
        Assert.Single(response.Result.Items);
        Assert.Equal(ApiMediaType.Paged, response.Result.Items[0].MediaType);
    }

    [Fact]
    public async Task Search_ReturnsUnauthenticated_ForInvalidApiKey()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        await using var pipelineServer = await PipelineServer.StartAsync();

        await using var apiHostFactory = new WebApplicationFactory<EMMA.ApiHost.ApiHostEntryPoint>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    var settings = new Dictionary<string, string?>
                    {
                        ["PluginHost:BaseUrl"] = pipelineServer.Address,
                        ["PluginHost:PluginId"] = "demo",
                        ["ApiAuth:Enabled"] = "true",
                        ["ApiAuth:Keys:0:Key"] = "test-key",
                        ["ApiAuth:Keys:0:ClientId"] = "test-client"
                    };

                    config.AddInMemoryCollection(settings);
                });
            });

        var httpClient = apiHostFactory.CreateClient();
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpClient = httpClient
        });

        var client = new PagedMediaApi.PagedMediaApiClient(channel);
        var headers = new Metadata
        {
            { "x-api-key", "invalid-key" }
        };

        await Assert.ThrowsAsync<RpcException>(() => client.SearchAsync(new SearchRequest
        {
            Query = "demo",
            Context = new ApiRequestContext
            {
                CorrelationId = "test",
                DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O"),
                ClientId = "test-client"
            }
        }, headers).ResponseAsync);
    }

    private sealed class PipelineServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private PipelineServer(WebApplication app, string address)
        {
            _app = app;
            Address = address;
        }

        public string Address { get; }

        public static async Task<PipelineServer> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0);
            });

            var app = builder.Build();
            app.MapGet("/pipeline/paged/search", (string? query) =>
            {
                if (!string.IsNullOrWhiteSpace(query)
                    && query.Contains("demo", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Ok(new[]
                    {
                        new
                        {
                            Id = "demo-1",
                            Source = "test",
                            Title = "Demo Paged",
                            MediaType = "paged"
                        }
                    });
                }

                return Results.Ok(Array.Empty<object>());
            });

            app.MapGet("/pipeline/paged/chapters", (string? mediaId) =>
            {
                if (string.Equals(mediaId, "demo-1", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Ok(new[]
                    {
                        new
                        {
                            Id = "ch-1",
                            Number = 1,
                            Title = "Chapter One"
                        }
                    });
                }

                return Results.Ok(Array.Empty<object>());
            });

            app.MapGet("/pipeline/paged/page", (string? mediaId, string? chapterId, int? index) =>
            {
                if (string.Equals(mediaId, "demo-1", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(chapterId, "ch-1", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Ok(new
                    {
                        Id = "page-1",
                        Index = index ?? 0,
                        ContentUri = "https://example.invalid/demo-1/page-1.jpg"
                    });
                }

                return Results.Ok(new
                {
                    Id = "page-0",
                    Index = index ?? 0,
                    ContentUri = "https://example.invalid/empty.jpg"
                });
            });

            await app.StartAsync();

            var address = GetServerAddress(app);
            return new PipelineServer(app, address);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static string GetServerAddress(IApplicationBuilder app)
    {
        var server = app.ApplicationServices.GetRequiredService<IServer>();
        var addressFeature = server.Features.Get<IServerAddressesFeature>();
        var address = addressFeature?.Addresses.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(address))
        {
            throw new InvalidOperationException("Failed to determine server address.");
        }

        return address;
    }
}
