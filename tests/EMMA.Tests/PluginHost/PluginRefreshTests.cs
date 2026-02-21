using System.Net;
using System.Text.Json;
using EMMA.Contracts.Plugins;
using EMMA.PluginHost.Plugins;
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

public sealed class PluginRefreshTests
{
    [Fact]
    public async Task RefreshEndpoint_RescansManifests()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var pluginApp = BuildPluginServer();
        await pluginApp.StartAsync();

        var address = GetServerAddress(pluginApp);
        var manifestPath = Path.Combine(tempRoot, "demo.plugin.json");
        await File.WriteAllTextAsync(manifestPath, $"{{\n  \"id\": \"demo\",\n  \"name\": \"Demo Plugin\",\n  \"version\": \"1.0.0\",\n  \"entry\": {{\n    \"protocol\": \"grpc\",\n    \"endpoint\": \"{address}\"\n  }}\n}}\n");

        await using var factory = new WebApplicationFactory<Program>()
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
        var response = await client.PostAsync("/plugins/refresh", content: null);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync();
        var snapshot = JsonSerializer.Deserialize<PluginRecord[]>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(snapshot);
        Assert.Single(snapshot!);
        Assert.Equal("demo", snapshot[0].Manifest.Id);
        Assert.True(snapshot[0].Status.Success);

        await pluginApp.StopAsync();
        try
        {
            Directory.Delete(tempRoot, true);
        }
        catch
        {
        }
    }

    private static WebApplication BuildPluginServer()
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
        app.MapGrpcService<MockPluginControlService>();

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

    private sealed class MockPluginControlService : PluginControl.PluginControlBase
    {
        public override Task<HealthResponse> GetHealth(HealthRequest request, ServerCallContext context)
        {
            return Task.FromResult(new HealthResponse
            {
                Status = "ok",
                Version = "1.0.0",
                Message = "mock"
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
}
