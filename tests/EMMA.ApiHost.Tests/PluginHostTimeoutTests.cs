using System.Net;
using System.Text.Json;
using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EMMA.ApiHost.Tests;

public sealed class PluginHostTimeoutTests
{
    [Fact]
    public async Task PluginHandshakeTimeout_MarksPluginUnhealthy()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        await using var pluginServer = await TimeoutPluginServer.StartAsync(TimeSpan.FromSeconds(3));

        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-pluginhost-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var manifestPath = Path.Combine(tempRoot, "timeout.plugin.json");
        var manifestJson = $$"""
                {
                    "id": "timeout-plugin",
                    "name": "Timeout Plugin",
                    "version": "1.0.0",
                    "entry": {
                        "protocol": "grpc",
                        "endpoint": "{{pluginServer.Address}}"
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
                        ["PluginHost:HandshakeTimeoutSeconds"] = "1"
                    };

                    config.AddInMemoryCollection(settings);
                });
            });

        var client = pluginHostFactory.CreateClient();
        var status = await WaitForStatusAsync(client, "timeout-plugin", TimeSpan.FromSeconds(5));

        Assert.Equal("unhealthy", status.State);
        Assert.Equal("timeout", status.RuntimeState);
        Assert.Equal("Handshake timed out.", status.Message);
    }

    private static async Task<PluginStatus> WaitForStatusAsync(HttpClient client, string pluginId, TimeSpan timeout)
    {
        var stopAt = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < stopAt)
        {
            var response = await client.GetAsync("/plugins/status");
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadAsStringAsync();
            var status = ParseStatus(payload, pluginId);
            if (status is not null)
            {
                return status;
            }

            await Task.Delay(200);
        }

        throw new InvalidOperationException("Timed out waiting for plugin status.");
    }

    private static PluginStatus? ParseStatus(string json, string pluginId)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var id = GetStringProperty(element, "id", "Id");
            if (string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase))
            {
                var state = GetStringProperty(element, "state", "State") ?? string.Empty;
                var runtime = GetStringProperty(element, "runtimeState", "RuntimeState") ?? string.Empty;
                var message = GetStringProperty(element, "message", "Message") ?? string.Empty;
                return new PluginStatus(state, runtime, message);
            }
        }

        return null;
    }

    private static string? GetStringProperty(JsonElement element, string camelName, string pascalName)
    {
        if (element.TryGetProperty(camelName, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (element.TryGetProperty(pascalName, out value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private sealed record PluginStatus(string State, string RuntimeState, string Message);

    private sealed class TimeoutPluginServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private TimeoutPluginServer(WebApplication app, string address)
        {
            _app = app;
            Address = address;
        }

        public string Address { get; }

        public static async Task<TimeoutPluginServer> StartAsync(TimeSpan delay)
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
            builder.Services.AddSingleton(new TimeoutPluginFixture(delay));

            var app = builder.Build();
            app.MapGrpcService<TimeoutPluginControlService>();

            await app.StartAsync();

            var address = GetServerAddress(app);
            return new TimeoutPluginServer(app, address);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed record TimeoutPluginFixture(TimeSpan Delay);

    private sealed class TimeoutPluginControlService(TimeoutPluginFixture fixture) : PluginControl.PluginControlBase
    {
        private readonly TimeoutPluginFixture _fixture = fixture;

        public override async Task<HealthResponse> GetHealth(HealthRequest request, ServerCallContext context)
        {
            await Task.Delay(_fixture.Delay, context.CancellationToken);
            return new HealthResponse
            {
                Status = "ok",
                Version = "1.0.0",
                Message = "late"
            };
        }

        public override async Task<CapabilitiesResponse> GetCapabilities(CapabilitiesRequest request, ServerCallContext context)
        {
            await Task.Delay(_fixture.Delay, context.CancellationToken);
            return new CapabilitiesResponse
            {
                Budgets = new CapabilityBudgets
                {
                    CpuBudgetMs = 100,
                    MemoryMb = 64
                },
                Permissions = new CapabilityPermissions()
            };
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
