using EMMA.Contracts.Plugins;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;

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
        var client = CreateClient();

        var response = await client.GetCapabilitiesAsync(new CapabilitiesRequest());

        Assert.Contains("health", response.Capabilities);
        Assert.Contains("capabilities", response.Capabilities);
    }

    private PluginControl.PluginControlClient CreateClient()
    {
        var httpClient = _factory.CreateDefaultClient();
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
