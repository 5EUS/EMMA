using EMMA.Contracts.Plugins;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using PluginHostProgram = global::Program;

namespace EMMA.Tests.PluginHost;

public sealed class PluginControlTests : IClassFixture<WebApplicationFactory<PluginHostProgram>>
{
    private readonly WebApplicationFactory<PluginHostProgram> _factory;

    public PluginControlTests(WebApplicationFactory<PluginHostProgram> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
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
        var client = CreateClient(_factory);

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

    private static PluginControl.PluginControlClient CreateClient(WebApplicationFactory<PluginHostProgram> factory)
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
