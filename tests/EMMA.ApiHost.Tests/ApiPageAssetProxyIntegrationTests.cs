using System.Net;
using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EMMA.ApiHost.Tests;

public sealed class ApiPageAssetProxyIntegrationTests
{
    [Fact]
    public async Task PageAsset_ProxiesThroughPluginHostPipeline()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var assetPayload = new byte[] { 9, 8, 7, 6 };
        await using var assetServer = await AssetServer.StartAsync(assetPayload, "application/octet-stream");
        await using var pluginServer = await PluginServer.StartAsync(assetServer.AssetUrl);

        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-apihost-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var manifestPath = Path.Combine(tempRoot, "demo.plugin.json");
        var manifestJson = $$"""
                {
                    "id": "demo",
                    "name": "Demo Plugin",
                    "version": "1.0.0",
                    "protocol": "grpc",
                    "endpoint": "{{pluginServer.Address}}",
                    "capabilities": {
                        "network": ["http"],
                        "cache": true
                    }
                }
                """;
        await File.WriteAllTextAsync(manifestPath, manifestJson);

        await using var pluginHostFactory = new WebApplicationFactory<EMMA.PluginHost.PluginHostEntryPoint>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    var settings = new Dictionary<string, string?>
                    {
                        ["PluginHost:ManifestDirectory"] = tempRoot,
                        ["PluginHost:HandshakeOnStartup"] = "true",
                        ["PluginHost:HandshakeTimeoutSeconds"] = "5"
                    };

                    config.AddInMemoryCollection(settings);
                });
            });

        var pluginHostClient = pluginHostFactory.CreateClient();
        var pluginHostBaseUrl = pluginHostClient.BaseAddress?.ToString().TrimEnd('/')
            ?? throw new InvalidOperationException("Plugin host base address missing.");

        var pluginHostHandler = pluginHostFactory.Server.CreateHandler();

        await using var apiHostFactory = new WebApplicationFactory<EMMA.ApiHost.ApiHostEntryPoint>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    var settings = new Dictionary<string, string?>
                    {
                        ["PluginHost:BaseUrl"] = pluginHostBaseUrl,
                        ["PluginHost:PluginId"] = "demo",
                        ["ApiAuth:Enabled"] = "true",
                        ["ApiAuth:Keys:0:Key"] = "test-key",
                        ["ApiAuth:Keys:0:ClientId"] = "test-client"
                    };

                    config.AddInMemoryCollection(settings);
                });

                builder.ConfigureServices(services =>
                {
                    services.AddHttpClient<EMMA.Infrastructure.Http.PluginHostPagedMediaPort>(client =>
                    {
                        client.BaseAddress = new Uri(pluginHostBaseUrl, UriKind.Absolute);
                    }).ConfigurePrimaryHttpMessageHandler(() => pluginHostHandler);
                });
            });

        var apiClient = apiHostFactory.CreateClient();
        apiClient.DefaultRequestHeaders.Add("x-api-key", "test-key");

        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            response = await apiClient.GetAsync("/api/paged/page-asset?mediaId=demo-1&chapterId=ch-1&index=0");
            if (response.IsSuccessStatusCode)
            {
                break;
            }

            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
            }

            await Task.Delay(200);
        }

        response ??= new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(assetPayload, bytes);
    }

    private sealed class AssetServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private AssetServer(WebApplication app, string url)
        {
            _app = app;
            AssetUrl = url;
        }

        public string AssetUrl { get; }

        public static async Task<AssetServer> StartAsync(byte[] payload, string contentType)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0);
            });

            var app = builder.Build();
            app.MapGet("/asset", () => Results.File(payload, contentType));

            await app.StartAsync();

            var address = GetServerAddress(app);
            return new AssetServer(app, $"{address}/asset");
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class PluginServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private PluginServer(WebApplication app, string address)
        {
            _app = app;
            Address = address;
        }

        public string Address { get; }

        public static async Task<PluginServer> StartAsync(string assetUrl)
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
            builder.Services.AddSingleton(new PluginFixture(assetUrl));

            var app = builder.Build();
            app.MapGrpcService<PluginControlService>();
            app.MapGrpcService<PageProviderService>();

            await app.StartAsync();

            var address = GetServerAddress(app);
            return new PluginServer(app, address);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed record PluginFixture(string AssetUrl);

    private sealed class PluginControlService : PluginControl.PluginControlBase
    {
        public override Task<HealthResponse> GetHealth(HealthRequest request, ServerCallContext context)
        {
            return Task.FromResult(new HealthResponse
            {
                Status = "ok",
                Version = "1.0.0",
                Message = "ok"
            });
        }

        public override Task<CapabilitiesResponse> GetCapabilities(CapabilitiesRequest request, ServerCallContext context)
        {
            var response = new CapabilitiesResponse
            {
                Budgets = new CapabilityBudgets
                {
                    CpuBudgetMs = 100,
                    MemoryMb = 64
                },
                Permissions = new CapabilityPermissions()
            };

            response.Capabilities.Add("health");
            response.Capabilities.Add("capabilities");
            return Task.FromResult(response);
        }
    }

    private sealed class PageProviderService(PluginFixture fixture) : PageProvider.PageProviderBase
    {
        private readonly PluginFixture _fixture = fixture;

        public override Task<PageResponse> GetPage(PageRequest request, ServerCallContext context)
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
