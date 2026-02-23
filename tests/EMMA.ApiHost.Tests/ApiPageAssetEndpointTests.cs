using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EMMA.ApiHost.Tests;

public sealed class ApiPageAssetEndpointTests
{
    [Fact]
    public async Task PageAsset_ProxiesPluginHost()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        await using var stub = await PluginHostStub.StartAsync(payload, "application/octet-stream");

        await using var factory = new WebApplicationFactory<EMMA.ApiHost.ApiHostEntryPoint>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    var settings = new Dictionary<string, string?>
                    {
                        ["PluginHost:BaseUrl"] = stub.Address
                    };

                    config.AddInMemoryCollection(settings);
                });
            });

        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/paged/page-asset?mediaId=demo-1&chapterId=ch-1&index=0");
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal("application/octet-stream", contentType);
        Assert.Equal(payload, bytes);
    }

    private sealed class PluginHostStub : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private PluginHostStub(WebApplication app, string address)
        {
            _app = app;
            Address = address;
        }

        public string Address { get; }

        public static async Task<PluginHostStub> StartAsync(byte[] payload, string contentType)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0);
            });

            var app = builder.Build();
            app.MapGet("/pipeline/paged/page-asset", () => Results.File(payload, contentType));

            await app.StartAsync();

            var address = GetServerAddress(app);
            return new PluginHostStub(app, address);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private static string GetServerAddress(IApplicationBuilder app)
        {
            var server = app.ApplicationServices.GetRequiredService<IServer>();
            var addressFeature = server.Features.Get<IServerAddressesFeature>();
            var address = addressFeature?.Addresses.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(address))
            {
                throw new InvalidOperationException("Failed to determine plugin host stub address.");
            }

            return address;
        }
    }
}
