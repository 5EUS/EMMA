using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EMMA.Domain;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Sandboxing;
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

public sealed class PluginRepositoryInstallOrchestratorTests
{
    [Fact]
    public async Task InstallFromRepositoryAsync_InstallsManifestAndPayload()
    {
        var tempRoot = CreateTempRoot();
        await using var server = BuildRepositoryServer();

        try
        {
            await server.App.StartAsync();
            var address = GetServerAddress(server.App);

            const string repositoryId = "demo-repo";
            const string pluginId = "demo.plugin";
            const string version = "1.2.3";

            var payloadBytes = Encoding.UTF8.GetBytes("wasm-payload");
            var archiveBytes = CreatePluginArchive(pluginId, version, payloadBytes);
            server.CatalogPayload =
                BuildCatalogJson(repositoryId, pluginId, version, $"{address}/artifacts/plugin.zip", ComputeSha256Hex(archiveBytes));
            server.ArtifactBytes = archiveBytes;

            var harness = CreateHarness(tempRoot);
            await harness.RepositoryService.AddRepositoryAsync(
                new AddPluginRepositoryRequest(
                    CatalogUrl: $"{address}/catalog.json",
                    RepositoryId: repositoryId,
                    Name: "Demo Repo"),
                CancellationToken.None);

            var result = await harness.Orchestrator.InstallFromRepositoryAsync(
                new InstallPluginFromRepositoryRequest(
                    RepositoryId: repositoryId,
                    PluginId: pluginId,
                    Version: version,
                    RefreshCatalog: false,
                    RescanAfterInstall: false),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(pluginId, result.PluginId);
            Assert.Equal(version, result.Version);
            Assert.Equal("wasm", result.PayloadType);

            var installedManifestPath = Path.Combine(harness.Options.Value.ManifestDirectory, $"{pluginId}.plugin.json");
            var installedPayloadPath = Path.Combine(harness.Options.Value.SandboxRootDirectory, pluginId, "plugin.wasm");

            Assert.True(File.Exists(installedManifestPath));
            Assert.True(File.Exists(installedPayloadPath));
            Assert.Equal(payloadBytes, await File.ReadAllBytesAsync(installedPayloadPath));
        }
        finally
        {
            await server.App.StopAsync();
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task InstallFromRepositoryAsync_RejectsWhenArtifactHashMismatches()
    {
        var tempRoot = CreateTempRoot();
        await using var server = BuildRepositoryServer();

        try
        {
            await server.App.StartAsync();
            var address = GetServerAddress(server.App);

            const string repositoryId = "demo-repo";
            const string pluginId = "demo.plugin";
            const string version = "2.0.0";

            var payloadBytes = Encoding.UTF8.GetBytes("tampered");
            var archiveBytes = CreatePluginArchive(pluginId, version, payloadBytes);
                server.CatalogPayload =
                BuildCatalogJson(
                    repositoryId,
                    pluginId,
                    version,
                    $"{address}/artifacts/plugin.zip",
                    new string('f', 64));
                server.ArtifactBytes = archiveBytes;

            var harness = CreateHarness(tempRoot);
            await harness.RepositoryService.AddRepositoryAsync(
                new AddPluginRepositoryRequest(
                    CatalogUrl: $"{address}/catalog.json",
                    RepositoryId: repositoryId,
                    Name: "Demo Repo"),
                CancellationToken.None);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                harness.Orchestrator.InstallFromRepositoryAsync(
                    new InstallPluginFromRepositoryRequest(
                        RepositoryId: repositoryId,
                        PluginId: pluginId,
                        Version: version,
                        RefreshCatalog: false,
                        RescanAfterInstall: false),
                    CancellationToken.None));

            Assert.Contains("hash mismatch", error.Message, StringComparison.OrdinalIgnoreCase);

            var installedManifestPath = Path.Combine(harness.Options.Value.ManifestDirectory, $"{pluginId}.plugin.json");
            var installedPluginDir = Path.Combine(harness.Options.Value.SandboxRootDirectory, pluginId);

            Assert.False(File.Exists(installedManifestPath));
            Assert.False(Directory.Exists(installedPluginDir));
        }
        finally
        {
            await server.App.StopAsync();
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task InstallFromRepositoryAsync_ReplacesExistingPluginFiles()
    {
        var tempRoot = CreateTempRoot();
        await using var server = BuildRepositoryServer();

        try
        {
            await server.App.StartAsync();
            var address = GetServerAddress(server.App);

            const string repositoryId = "demo-repo";
            const string pluginId = "demo.plugin";
            const string version = "3.0.0";

            var payloadBytes = Encoding.UTF8.GetBytes("new-payload");
            var archiveBytes = CreatePluginArchive(pluginId, version, payloadBytes);
            server.CatalogPayload =
                BuildCatalogJson(repositoryId, pluginId, version, $"{address}/artifacts/plugin.zip", ComputeSha256Hex(archiveBytes));
            server.ArtifactBytes = archiveBytes;

            var harness = CreateHarness(tempRoot);

            Directory.CreateDirectory(harness.Options.Value.ManifestDirectory);
            Directory.CreateDirectory(Path.Combine(harness.Options.Value.SandboxRootDirectory, pluginId));

            var oldManifestPath = Path.Combine(harness.Options.Value.ManifestDirectory, $"{pluginId}.plugin.json");
            var oldFilePath = Path.Combine(harness.Options.Value.SandboxRootDirectory, pluginId, "old.txt");
            await File.WriteAllTextAsync(oldManifestPath, "{\"id\":\"demo.plugin\",\"version\":\"0.9.0\"}");
            await File.WriteAllTextAsync(oldFilePath, "legacy");

            await harness.RepositoryService.AddRepositoryAsync(
                new AddPluginRepositoryRequest(
                    CatalogUrl: $"{address}/catalog.json",
                    RepositoryId: repositoryId,
                    Name: "Demo Repo"),
                CancellationToken.None);

            var result = await harness.Orchestrator.InstallFromRepositoryAsync(
                new InstallPluginFromRepositoryRequest(
                    RepositoryId: repositoryId,
                    PluginId: pluginId,
                    Version: version,
                    RefreshCatalog: false,
                    RescanAfterInstall: false),
                CancellationToken.None);

            Assert.True(result.Success);

            var installedPayloadPath = Path.Combine(harness.Options.Value.SandboxRootDirectory, pluginId, "plugin.wasm");
            Assert.True(File.Exists(installedPayloadPath));
            Assert.Equal(payloadBytes, await File.ReadAllBytesAsync(installedPayloadPath));
            Assert.False(File.Exists(oldFilePath));
        }
        finally
        {
            await server.App.StopAsync();
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task InstallFromRepositoryAsync_AllowsWasmTaggedReleasePlatforms()
    {
        var tempRoot = CreateTempRoot();
        await using var server = BuildRepositoryServer();

        try
        {
            await server.App.StartAsync();
            var address = GetServerAddress(server.App);

            const string repositoryId = "demo-repo";
            const string pluginId = "demo.plugin";
            const string version = "4.0.0-wasm";
            const string manifestVersion = "4.0.0";

            var payloadBytes = Encoding.UTF8.GetBytes("wasm-payload");
            var archiveBytes = CreatePluginArchive(pluginId, manifestVersion, payloadBytes);
            server.CatalogPayload = BuildCatalogJson(
                repositoryId,
                pluginId,
                version,
                $"{address}/artifacts/plugin.zip",
                ComputeSha256Hex(archiveBytes),
                ["wasm"]);
            server.ArtifactBytes = archiveBytes;

            var harness = CreateHarness(tempRoot);
            await harness.RepositoryService.AddRepositoryAsync(
                new AddPluginRepositoryRequest(
                    CatalogUrl: $"{address}/catalog.json",
                    RepositoryId: repositoryId,
                    Name: "Demo Repo"),
                CancellationToken.None);

            var result = await harness.Orchestrator.InstallFromRepositoryAsync(
                new InstallPluginFromRepositoryRequest(
                    RepositoryId: repositoryId,
                    PluginId: pluginId,
                    Version: version,
                    RefreshCatalog: false,
                    RescanAfterInstall: false),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(manifestVersion, result.Version);
            Assert.Equal("wasm", result.PayloadType);
        }
        finally
        {
            await server.App.StopAsync();
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task InstallFromRepositoryAsync_RejectsReleaseAliasWhenSuffixIsNotPlatform()
    {
        var tempRoot = CreateTempRoot();
        await using var server = BuildRepositoryServer();

        try
        {
            await server.App.StartAsync();
            var address = GetServerAddress(server.App);

            const string repositoryId = "demo-repo";
            const string pluginId = "demo.plugin";
            const string releaseVersion = "4.1.0-experimental";
            const string manifestVersion = "4.1.0";

            var payloadBytes = Encoding.UTF8.GetBytes("wasm-payload");
            var archiveBytes = CreatePluginArchive(pluginId, manifestVersion, payloadBytes);
            server.CatalogPayload = BuildCatalogJson(
                repositoryId,
                pluginId,
                releaseVersion,
                $"{address}/artifacts/plugin.zip",
                ComputeSha256Hex(archiveBytes),
                ["wasm"]);
            server.ArtifactBytes = archiveBytes;

            var harness = CreateHarness(tempRoot);
            await harness.RepositoryService.AddRepositoryAsync(
                new AddPluginRepositoryRequest(
                    CatalogUrl: $"{address}/catalog.json",
                    RepositoryId: repositoryId,
                    Name: "Demo Repo"),
                CancellationToken.None);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                harness.Orchestrator.InstallFromRepositoryAsync(
                    new InstallPluginFromRepositoryRequest(
                        RepositoryId: repositoryId,
                        PluginId: pluginId,
                        Version: releaseVersion,
                        RefreshCatalog: false,
                        RescanAfterInstall: false),
                    CancellationToken.None));

            Assert.Contains("Plugin version mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await server.App.StopAsync();
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task InstallFromRepositoryAsync_RejectsMismatchedOsPlatformTag()
    {
        var tempRoot = CreateTempRoot();
        await using var server = BuildRepositoryServer();

        try
        {
            await server.App.StartAsync();
            var address = GetServerAddress(server.App);

            const string repositoryId = "demo-repo";
            const string pluginId = "demo.plugin";
            const string version = "5.0.0";

            var payloadBytes = Encoding.UTF8.GetBytes("wasm-payload");
            var archiveBytes = CreatePluginArchive(pluginId, version, payloadBytes);
            server.CatalogPayload = BuildCatalogJson(
                repositoryId,
                pluginId,
                version,
                $"{address}/artifacts/plugin.zip",
                ComputeSha256Hex(archiveBytes),
                [GetMismatchedPlatformTag()]);
            server.ArtifactBytes = archiveBytes;

            var harness = CreateHarness(tempRoot);
            await harness.RepositoryService.AddRepositoryAsync(
                new AddPluginRepositoryRequest(
                    CatalogUrl: $"{address}/catalog.json",
                    RepositoryId: repositoryId,
                    Name: "Demo Repo"),
                CancellationToken.None);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                harness.Orchestrator.InstallFromRepositoryAsync(
                    new InstallPluginFromRepositoryRequest(
                        RepositoryId: repositoryId,
                        PluginId: pluginId,
                        Version: version,
                        RefreshCatalog: false,
                        RescanAfterInstall: false),
                    CancellationToken.None));

            Assert.Contains("does not target platform", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await server.App.StopAsync();
            TryDeleteDirectory(tempRoot);
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "emma-plugin-install-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static RepositoryServer BuildRepositoryServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
        });

        var app = builder.Build();
        var server = new RepositoryServer(app);

        app.MapGet("/catalog.json", () => Results.Text(server.CatalogPayload, "application/json"));
        app.MapGet("/artifacts/plugin.zip", () => Results.File(server.ArtifactBytes, "application/zip"));

        return server;
    }

    private static string BuildCatalogJson(
        string repositoryId,
        string pluginId,
        string version,
        string assetUrl,
        string sha256,
        IReadOnlyList<string>? platforms = null)
    {
        var catalog = new PluginRepositoryCatalog(
            RepositoryId: repositoryId,
            Name: "Demo Repository",
            Plugins:
            [
                new PluginRepositoryCatalogPlugin(
                    PluginId: pluginId,
                    Name: "Demo Plugin",
                    Description: "Demo",
                    Author: "EMMA",
                    SourceRepositoryUrl: "https://example.com/demo",
                    Releases:
                    [
                        new PluginRepositoryCatalogRelease(
                            Version: version,
                            AssetUrl: assetUrl,
                            Sha256: sha256,
                            Platforms: platforms,
                            PublishedAtUtc: null,
                            IsPrerelease: false,
                            Notes: null)
                    ])
            ]);

        return JsonSerializer.Serialize(catalog, PluginRepositoryJsonContext.Default.PluginRepositoryCatalog);
    }

    private static byte[] CreatePluginArchive(string pluginId, string version, byte[] payloadBytes)
    {
        var manifestJson = JsonSerializer.Serialize(new
        {
            id = pluginId,
            name = "Demo Plugin",
            version,
            protocol = "grpc",
            endpoint = "http://127.0.0.1:50099"
        });

        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry($"manifest/{pluginId}.json", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(manifestJson);
            }

            var payloadEntry = archive.CreateEntry($"{pluginId}/plugin.wasm", CompressionLevel.NoCompression);
            using var payloadStream = payloadEntry.Open();
            payloadStream.Write(payloadBytes, 0, payloadBytes.Length);
        }

        return memory.ToArray();
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetMismatchedPlatformTag()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "linux";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "windows";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "linux";
        }

        return "windows";
    }

    private static string GetServerAddress(IApplicationBuilder app)
    {
        var server = app.ApplicationServices.GetRequiredService<IServer>();
        var addressFeature = server.Features.Get<IServerAddressesFeature>();
        var address = addressFeature?.Addresses.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(address))
        {
            throw new InvalidOperationException("Failed to determine repository server address.");
        }

        return address;
    }

    private static TestHarness CreateHarness(string tempRoot)
    {
        var options = Options.Create(new PluginHostOptions
        {
            ManifestDirectory = Path.Combine(tempRoot, "manifests"),
            SandboxRootDirectory = Path.Combine(tempRoot, "plugins"),
            RepositoryDirectory = Path.Combine(tempRoot, "repositories"),
            AllowNoSandboxFallback = true,
            AllowInsecureRepositoryHttp = true,
            HandshakeOnStartup = false,
            RepositoryRequestTimeoutSeconds = 10,
            RepositoryMaxCatalogBytes = 1024 * 1024,
            RepositoryMaxArtifactBytes = 1024 * 1024 * 64
        });

        var signatureOptions = Options.Create(new PluginSignatureOptions
        {
            RequireSignedPlugins = false
        });

        var sanitizer = new PluginPermissionSanitizer(options, NullLogger<PluginPermissionSanitizer>.Instance);
        var loader = new PluginManifestLoader(options, sanitizer, NullLogger<PluginManifestLoader>.Instance);
        var registry = new PluginRegistry();
        var sandbox = new NoOpPluginSandboxManager(options, NullLogger<NoOpPluginSandboxManager>.Instance);
        var resolver = new PluginEntrypointResolver(options);
        var signatureVerifier = new HmacPluginSignatureVerifier(signatureOptions);
        var endpointAllocator = new PluginEndpointAllocator();
        var processManager = new PluginProcessManager(
            options,
            sandbox,
            resolver,
            signatureOptions,
            signatureVerifier,
            NullLogger<PluginProcessManager>.Instance);
        var handshakeService = new PluginHandshakeService(
            loader,
            registry,
            sandbox,
            processManager,
            endpointAllocator,
            new NoOpWasmPluginRuntimeHost(),
            options,
            NullLogger<PluginHandshakeService>.Instance);

        var store = new PluginRepositoryStore(options);
        var client = new PluginRepositoryCatalogClient(options, NullLogger<PluginRepositoryCatalogClient>.Instance);
        var repositoryService = new PluginRepositoryService(store, client, NullLogger<PluginRepositoryService>.Instance);
        var orchestrator = new PluginRepositoryInstallOrchestrator(
            repositoryService,
            client,
            processManager,
            handshakeService,
            signatureVerifier,
            signatureOptions,
            options,
            NullLogger<PluginRepositoryInstallOrchestrator>.Instance);

        return new TestHarness(options, repositoryService, orchestrator);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // ignored in tests
        }
    }

    private sealed record TestHarness(
        IOptions<PluginHostOptions> Options,
        PluginRepositoryService RepositoryService,
        PluginRepositoryInstallOrchestrator Orchestrator);

    private sealed class RepositoryServer(WebApplication app) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public string CatalogPayload { get; set; } = string.Empty;
        public byte[] ArtifactBytes { get; set; } = [];

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
        }
    }

    private sealed class NoOpWasmPluginRuntimeHost : IWasmPluginRuntimeHost
    {
        public bool IsWasmPlugin(PluginManifest manifest) => false;

        public Task WarmupAsync(PluginManifest manifest, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<PluginHandshakeStatus> HandshakeAsync(PluginManifest manifest, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<MediaSummary>> SearchAsync(PluginRecord record, string query, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<string> SearchJsonAsync(PluginRecord record, string query, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<string> BenchmarkAsync(PluginRecord record, int iterations, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<string> BenchmarkNetworkAsync(PluginRecord record, string query, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(PluginRecord record, MediaId mediaId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<MediaPage> GetPageAsync(PluginRecord record, MediaId mediaId, string chapterId, int pageIndex, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<MediaPagesResult> GetPagesAsync(PluginRecord record, MediaId mediaId, string chapterId, int startIndex, int count, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
