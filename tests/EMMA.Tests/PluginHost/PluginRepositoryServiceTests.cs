using System.Net;
using System.Text.Json;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EMMA.Tests.PluginHost;

public sealed class PluginRepositoryServiceTests
{
    [Fact]
    public async Task AddRepositoryAsync_PersistsRepositoryAndCatalog()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-plugin-repo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var catalog = new PluginRepositoryCatalog(
            RepositoryId: "demo-repo",
            Name: "Demo Repository",
            Plugins:
            [
                new PluginRepositoryCatalogPlugin(
                    PluginId: "demo.plugin",
                    Name: "Demo Plugin",
                    Description: "demo",
                    Author: "EMMA",
                    SourceRepositoryUrl: "https://github.com/example/demo-plugin",
                    Releases:
                    [
                        new PluginRepositoryCatalogRelease(
                            Version: "1.0.0",
                            AssetUrl: "http://localhost/assets/demo-plugin-1.0.0.zip",
                            Sha256: new string('a', 64),
                            Platforms: ["linux"]) 
                    ])
            ]);

        await using var server = BuildCatalogServer(catalog);
        await server.StartAsync();

        var catalogUrl = $"{GetServerAddress(server)}/catalog.json";

        var options = CreateOptions(tempRoot);
        var store = new PluginRepositoryStore(options);
        var client = new PluginRepositoryCatalogClient(options, NullLogger<PluginRepositoryCatalogClient>.Instance);
        var service = new PluginRepositoryService(store, client, NullLogger<PluginRepositoryService>.Instance);

        var repository = await service.AddRepositoryAsync(
            new AddPluginRepositoryRequest(
                CatalogUrl: catalogUrl,
                RepositoryId: "demo-repo",
                Name: "Demo Repository"),
            CancellationToken.None);

        var repositories = await service.ListRepositoriesAsync(CancellationToken.None);
        var snapshot = await service.GetRepositoryCatalogSnapshotAsync(
            repository.Id,
            refresh: false,
            CancellationToken.None);

        Assert.Single(repositories);
        Assert.Equal("demo-repo", repositories[0].Id);
        Assert.Equal("Demo Repository", repositories[0].Name);
        Assert.Equal("demo-repo", snapshot.Catalog.RepositoryId);
        Assert.Single(snapshot.Catalog.Plugins);
        Assert.Equal("demo.plugin", snapshot.Catalog.Plugins[0].PluginId);
    }

    [Fact]
    public async Task ResolvePluginReleaseAsync_SelectsLatestStableReleaseByDefault()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-plugin-repo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var catalog = new PluginRepositoryCatalog(
            RepositoryId: "stable-repo",
            Name: "Stable Repository",
            Plugins:
            [
                new PluginRepositoryCatalogPlugin(
                    PluginId: "stable.plugin",
                    Name: "Stable Plugin",
                    Description: null,
                    Author: null,
                    SourceRepositoryUrl: null,
                    Releases:
                    [
                        new PluginRepositoryCatalogRelease(
                            Version: "1.0.0",
                            AssetUrl: "http://localhost/assets/stable-plugin-1.0.0.zip",
                            Sha256: new string('b', 64),
                            Platforms: ["linux"]),
                        new PluginRepositoryCatalogRelease(
                            Version: "2.0.0",
                            AssetUrl: "http://localhost/assets/stable-plugin-2.0.0.zip",
                            Sha256: new string('c', 64),
                            Platforms: ["linux"],
                            IsPrerelease: true),
                        new PluginRepositoryCatalogRelease(
                            Version: "1.2.0",
                            AssetUrl: "http://localhost/assets/stable-plugin-1.2.0.zip",
                            Sha256: new string('d', 64),
                            Platforms: ["linux"])
                    ])
            ]);

        await using var server = BuildCatalogServer(catalog);
        await server.StartAsync();

        var options = CreateOptions(tempRoot);
        var store = new PluginRepositoryStore(options);
        var client = new PluginRepositoryCatalogClient(options, NullLogger<PluginRepositoryCatalogClient>.Instance);
        var service = new PluginRepositoryService(store, client, NullLogger<PluginRepositoryService>.Instance);

        await service.AddRepositoryAsync(
            new AddPluginRepositoryRequest(
                CatalogUrl: $"{GetServerAddress(server)}/catalog.json",
                RepositoryId: "stable-repo"),
            CancellationToken.None);

        var selection = await service.ResolvePluginReleaseAsync(
            repositoryId: "stable-repo",
            pluginId: "stable.plugin",
            version: null,
            refreshCatalog: false,
            cancellationToken: CancellationToken.None);

        Assert.Equal("1.2.0", selection.Release.Version);
    }

    [Fact]
    public async Task AddRepositoryAsync_ThrowsWhenCatalogRepositoryIdMismatches()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-plugin-repo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var catalog = new PluginRepositoryCatalog(
            RepositoryId: "unexpected",
            Name: "Mismatch Repository",
            Plugins:
            [
                new PluginRepositoryCatalogPlugin(
                    PluginId: "demo.plugin",
                    Name: "Demo Plugin",
                    Description: null,
                    Author: null,
                    SourceRepositoryUrl: null,
                    Releases:
                    [
                        new PluginRepositoryCatalogRelease(
                            Version: "1.0.0",
                            AssetUrl: "http://localhost/assets/demo-plugin-1.0.0.zip",
                            Sha256: new string('e', 64),
                            Platforms: ["linux"])
                    ])
            ]);

        await using var server = BuildCatalogServer(catalog);
        await server.StartAsync();

        var options = CreateOptions(tempRoot);
        var store = new PluginRepositoryStore(options);
        var client = new PluginRepositoryCatalogClient(options, NullLogger<PluginRepositoryCatalogClient>.Instance);
        var service = new PluginRepositoryService(store, client, NullLogger<PluginRepositoryService>.Instance);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.AddRepositoryAsync(
                new AddPluginRepositoryRequest(
                    CatalogUrl: $"{GetServerAddress(server)}/catalog.json",
                    RepositoryId: "expected"),
                CancellationToken.None));
    }

    private static IOptions<PluginHostOptions> CreateOptions(string tempRoot)
    {
        return Options.Create(new PluginHostOptions
        {
            ManifestDirectory = Path.Combine(tempRoot, "manifests"),
            SandboxRootDirectory = Path.Combine(tempRoot, "plugins"),
            RepositoryDirectory = Path.Combine(tempRoot, "repositories"),
            AllowInsecureRepositoryHttp = true,
            RepositoryRequestTimeoutSeconds = 10,
            RepositoryMaxCatalogBytes = 1024 * 1024,
            RepositoryMaxArtifactBytes = 1024 * 1024 * 32
        });
    }

    private static WebApplication BuildCatalogServer(PluginRepositoryCatalog catalog)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
        });

        var app = builder.Build();
        var payload = JsonSerializer.Serialize(catalog, PluginRepositoryJsonContext.Default.PluginRepositoryCatalog);
        app.MapGet("/catalog.json", () => Results.Text(payload, "application/json"));
        return app;
    }

    private static string GetServerAddress(IApplicationBuilder app)
    {
        var server = app.ApplicationServices.GetRequiredService<IServer>();
        var addressFeature = server.Features.Get<IServerAddressesFeature>();
        var address = addressFeature?.Addresses.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(address))
        {
            throw new InvalidOperationException("Failed to determine catalog server address.");
        }

        return address;
    }
}
