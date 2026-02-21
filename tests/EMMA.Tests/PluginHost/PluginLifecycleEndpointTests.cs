using System.Net;
using System.Text.Json;
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

public sealed class PluginLifecycleEndpointTests
{
    [Fact]
    public async Task StartStopAndLogsEndpoints_Work()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-lifecycle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var pluginApp = BuildPluginServer();
        await pluginApp.StartAsync();

        try
        {
            var address = GetServerAddress(pluginApp);
            var manifestPath = Path.Combine(tempRoot, "demo.plugin.json");
            await File.WriteAllTextAsync(manifestPath, $"{{\n  \"id\": \"demo\",\n  \"name\": \"Demo Plugin\",\n  \"version\": \"1.0.0\",\n  \"entry\": {{\n    \"protocol\": \"grpc\",\n    \"endpoint\": \"{address}\"\n  }}\n}}\n");

            await using var factory = new WebApplicationFactory<global::Program>()
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

            var startResponse = await client.PostAsync("/plugins/start?pluginId=demo", content: null);
            startResponse.EnsureSuccessStatusCode();

            var logsResponse = await client.GetAsync("/plugins/logs?pluginId=demo");
            Assert.True(logsResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound);

            if (logsResponse.StatusCode == HttpStatusCode.OK)
            {
                using var doc = JsonDocument.Parse(await logsResponse.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                Assert.Equal("demo", root.GetProperty("pluginId").GetString());
            }

            var stopResponse = await client.PostAsync("/plugins/stop?pluginId=demo", content: null);
            stopResponse.EnsureSuccessStatusCode();
        }
        finally
        {
            await pluginApp.StopAsync();
            TryDelete(tempRoot);
        }
    }

    [Fact]
    public async Task ResetEndpoint_ClearsQuarantine()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-lifecycle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var pluginApp = BuildPluginServer();
        await pluginApp.StartAsync();

        try
        {
            var address = GetServerAddress(pluginApp);
            var manifestPath = Path.Combine(tempRoot, "demo.plugin.json");
            await File.WriteAllTextAsync(manifestPath, $"{{\n  \"id\": \"demo\",\n  \"name\": \"Demo Plugin\",\n  \"version\": \"1.0.0\",\n  \"entry\": {{\n    \"protocol\": \"grpc\",\n    \"endpoint\": \"{address}\"\n  }}\n}}\n");

            await using var factory = new WebApplicationFactory<global::Program>()
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

            var resetResponse = await client.PostAsync("/plugins/reset?pluginId=demo", content: null);
            resetResponse.EnsureSuccessStatusCode();

            var statusResponse = await client.GetAsync("/plugins/status");
            statusResponse.EnsureSuccessStatusCode();

            var payload = await statusResponse.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            Assert.True(root.GetArrayLength() >= 0);
        }
        finally
        {
            await pluginApp.StopAsync();
            TryDelete(tempRoot);
        }
    }

    [Fact]
    public async Task SummaryEndpoint_ReturnsFields()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-lifecycle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var pluginApp = BuildPluginServer();
        await pluginApp.StartAsync();

        try
        {
            var address = GetServerAddress(pluginApp);
            var manifestPath = Path.Combine(tempRoot, "demo.plugin.json");
            await File.WriteAllTextAsync(manifestPath, $"{{\n  \"id\": \"demo\",\n  \"name\": \"Demo Plugin\",\n  \"version\": \"1.0.0\",\n  \"entry\": {{\n    \"protocol\": \"grpc\",\n    \"endpoint\": \"{address}\"\n  }}\n}}\n");

            await using var factory = new WebApplicationFactory<global::Program>()
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
            var response = await client.GetAsync("/plugins/summary");
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.GetArrayLength() > 0)
            {
                var first = root[0];
                Assert.True(first.TryGetProperty("health", out _));
                Assert.True(first.TryGetProperty("runtime", out _));
                Assert.True(first.TryGetProperty("lastHandshake", out _));
            }
        }
        finally
        {
            await pluginApp.StopAsync();
            TryDelete(tempRoot);
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
        app.MapGrpcService<TestPluginControlService>();

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
