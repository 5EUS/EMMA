using EMMA.Contracts.Plugins;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace EMMA.Tests.PluginHost;

public sealed class PluginControlTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PluginControlTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetHealth_ReturnsOkStatus()
    {
        var client = CreateClient();

        var response = await client.GetHealthAsync(new HealthRequest());

        Assert.Equal("ok", response.Status);
        Assert.False(string.IsNullOrWhiteSpace(response.Version));
    }

    [Fact]
    public async Task GetCapabilities_ReportsEndpoints()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["PluginHost:HandshakeOnStartup"] = "false",
                    ["PluginHost:ManifestDirectory"] = "",
                    ["PluginHost:HandshakeTimeoutSeconds"] = "5"
                };

                config.AddInMemoryCollection(settings);
            });
        });

        var client = CreateClient(factory);

        var response = await client.GetCapabilitiesAsync(new CapabilitiesRequest());

        Assert.Contains("health", response.Capabilities);
        Assert.Contains("capabilities", response.Capabilities);
        Assert.NotNull(response.Budgets);
        Assert.Equal(0, response.Budgets.CpuBudgetMs);
        Assert.Equal(0, response.Budgets.MemoryMb);
        Assert.NotNull(response.Permissions);
        Assert.Empty(response.Permissions.Domains);
        Assert.Empty(response.Permissions.Paths);
    }

    private PluginControl.PluginControlClient CreateClient()
    {
        return CreateClient(_factory);
    }

    private static PluginControl.PluginControlClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var httpClient = factory.CreateDefaultClient();
        httpClient.DefaultRequestVersion = new Version(2, 0);
        httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;

        var address = httpClient.BaseAddress ?? new Uri("http://localhost");
        var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpClient = httpClient
        });

        return new PluginControl.PluginControlClient(channel);
    }
}
