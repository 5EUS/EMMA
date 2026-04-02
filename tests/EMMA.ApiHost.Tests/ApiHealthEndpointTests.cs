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

public sealed class ApiHealthEndpointTests
{
    [Fact]
    public async Task Health_ReturnsOk()
    {
        await using var pluginHostStub = await PluginHostStub.StartAsync();

        await using var factory = CreateFactory(pluginHostStub.Address);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_ReturnsOk_WhenPluginHostIsReachable()
    {
        await using var pluginHostStub = await PluginHostStub.StartAsync();

        await using var factory = CreateFactory(pluginHostStub.Address);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static WebApplicationFactory<EMMA.ApiHost.ApiHostEntryPoint> CreateFactory(string pluginHostBaseUrl)
    {
        return new WebApplicationFactory<EMMA.ApiHost.ApiHostEntryPoint>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    var settings = new Dictionary<string, string?>
                    {
                        ["PluginHost:BaseUrl"] = pluginHostBaseUrl,
                        ["ApiAuth:Enabled"] = "false"
                    };

                    config.AddInMemoryCollection(settings);
                });
            });
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

        public static async Task<PluginHostStub> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0);
            });

            var app = builder.Build();
            app.MapGet("/", () => Results.Ok(new { status = "ok" }));
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
