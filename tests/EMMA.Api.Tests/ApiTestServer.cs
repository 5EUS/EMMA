using System.Net.Http.Headers;
using System.Text;

using EMMA.Api.Security;
using EMMA.Application.Pipelines;
using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.PluginHost.Install;
using EMMA.PluginHost.Policy;
using EMMA.PluginHost.Signing;
using EMMA.TestSupport;

using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace EMMA.Api.Tests;

/// <summary>
/// The real API host on a loopback port, with a fake plugin gateway and asset fetcher and a real SQLite database
/// in a temporary directory. Everything else is the production wiring, so a test exercises authentication, rate
/// limiting, routing, validation and storage exactly as a client would.
/// </summary>
internal sealed class ApiTestServer : IAsyncDisposable
{
    /// <summary>The key of the first configured client.</summary>
    public const string AliceKey = "alice-test-key";

    /// <summary>The key of the second configured client.</summary>
    public const string BobKey = "bob-test-key";

    private readonly string directory;
    private readonly HttpClient http;

    private ApiTestServer(WebApplication app, string directory, FakePluginGateway gateway, FakeAssetFetcher fetcher)
    {
        this.App = app;
        this.directory = directory;
        this.Gateway = gateway;
        this.Fetcher = fetcher;
        this.http = new HttpClient { BaseAddress = new Uri(app.Urls.First(), UriKind.Absolute) };
    }

    /// <summary>Gets the running application.</summary>
    public WebApplication App { get; }

    /// <summary>Gets the gateway the pipelines call.</summary>
    public FakePluginGateway Gateway { get; }

    /// <summary>Gets the fetcher that serves remote assets.</summary>
    public FakeAssetFetcher Fetcher { get; }

    /// <summary>Starts a server whose plugin gateway is a fake.</summary>
    /// <param name="perClientPermits">The per-client request allowance for the window.</param>
    /// <param name="installPlugin">Whether to record <see cref="TestData.Plugin"/> as installed and enabled.</param>
    /// <returns>The running server.</returns>
    public static async Task<ApiTestServer> StartAsync(int perClientPermits = 1000, bool installPlugin = true)
    {
        FakePluginGateway gateway = new();
        FakeAssetFetcher fetcher = new(TimeProvider.System);
        string root = NewRoot();
        WebApplication app = await BuildAsync(
            root,
            perClientPermits,
            [],
            options => options with
            {
                GatewayOverride = gateway,
                FetcherOverride = fetcher,

                // One attempt, so a failing fake answers at once instead of after the retry delays.
                CallOptions = new PluginCallOptions(1, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(30)),
            }).ConfigureAwait(false);
        if (installPlugin)
        {
            await InstallAsync(app, TestData.Plugin).ConfigureAwait(false);
        }

        return new ApiTestServer(app, root, gateway, fetcher);
    }

    /// <summary>
    /// Starts a server with the production plugin host: signed packages, the native Wasm runtime and the real
    /// gateway. The test repository's root key is trusted and its delegation file is in place, and the clock is the
    /// test PKI's, so signatures are in date.
    /// </summary>
    /// <returns>The running server.</returns>
    public static async Task<ApiTestServer> StartWithWasmAsync()
    {
        string root = NewRoot();
        string delegations = Path.Combine(root, "delegations");
        Directory.CreateDirectory(delegations);
        await File.WriteAllBytesAsync(Path.Combine(delegations, $"{TestPki.RepositoryId}.delegations.json"), DelegationJson.Write(TestPki.Bundle())).ConfigureAwait(false);
        WebApplication app = await BuildAsync(
            root,
            1000,
            [
                $"--Emma:Trust:Repositories:0:Id={TestPki.RepositoryId}",
                $"--Emma:Trust:Repositories:0:RootKeys:{TestPki.RootKeyId}={TestPki.Root.PublicPem}",
            ],
            options => options with { Time = new FakeTimeProvider(TestPki.Now) }).ConfigureAwait(false);
        return new ApiTestServer(app, root, new FakePluginGateway(), new FakeAssetFetcher(TimeProvider.System));
    }

    /// <summary>Sends a request.</summary>
    /// <param name="method">The method.</param>
    /// <param name="path">The path and query.</param>
    /// <param name="key">The API key to send, or <see langword="null"/> for none.</param>
    /// <param name="json">A JSON body, or <see langword="null"/> for none.</param>
    /// <param name="configure">Adjusts the request before it is sent.</param>
    /// <returns>The response.</returns>
    public async Task<ApiResponse> SendAsync(
        HttpMethod method,
        string path,
        string? key = AliceKey,
        string? json = null,
        Action<HttpRequestMessage>? configure = null)
    {
        using HttpRequestMessage request = new(method, path);
        if (key is not null)
        {
            request.Headers.Add("x-api-key", key);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        configure?.Invoke(request);
        using HttpResponseMessage response = await this.http.SendAsync(request).ConfigureAwait(false);
        byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        return new ApiResponse(response.StatusCode, response.Headers, response.Content.Headers, body);
    }

    /// <summary>Sends a GET as the first client.</summary>
    /// <param name="path">The path and query.</param>
    /// <param name="key">The API key to send.</param>
    /// <returns>The response.</returns>
    public Task<ApiResponse> GetAsync(string path, string? key = AliceKey) => this.SendAsync(HttpMethod.Get, path, key);

    /// <summary>Records a plugin as installed and enabled.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <returns>A task that completes when it is recorded.</returns>
    public Task InstallAsync(PluginId plugin) => InstallAsync(this.App, plugin);

    /// <summary>Records nodes as seen, as a search or browse would.</summary>
    /// <param name="nodes">The nodes.</param>
    /// <returns>A task that completes when they are stored.</returns>
    public Task RememberAsync(params Node[] nodes) =>
        this.App.Services.GetRequiredService<INodeStorePort>().UpsertAsync(nodes, CancellationToken.None);

    /// <summary>Installs a signed package through the same installer the CLI will use, then refreshes the permission snapshot.</summary>
    /// <param name="package">The archive.</param>
    /// <returns>The install result.</returns>
    public async Task<Outcome<InstalledPlugin>> InstallPackageAsync(byte[] package)
    {
        IServiceProvider services = this.App.Services;
        IPluginRegistryPort registry = services.GetRequiredService<IPluginRegistryPort>();
        PluginPackageInstaller installer = new(
            services.GetRequiredService<PluginHostOptions>(),
            services.GetRequiredService<TrustPolicy>(),
            services.GetRequiredService<PluginSignatureVerifier>(),
            registry,
            services.GetRequiredService<TimeProvider>());
        using MemoryStream stream = new(package);
        Outcome<InstalledPlugin> installed = await installer.InstallAsync(stream, TestPki.RepositoryId, CancellationToken.None).ConfigureAwait(false);
        await services.GetRequiredService<ManifestCapabilityPolicyProvider>().RefreshAsync(registry, CancellationToken.None).ConfigureAwait(false);
        return installed;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        this.http.Dispose();
        await this.App.DisposeAsync().ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    private static Task InstallAsync(WebApplication app, PluginId plugin)
    {
        IPluginRegistryPort registry = app.Services.GetRequiredService<IPluginRegistryPort>();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        InstalledPlugin record = new(plugin, "1.0.0", PluginStatus.Enabled, TestManifests.Json(TestManifests.Valid(plugin.Value)), null, now, now);
        return RecordAsync(app, registry, record);
    }

    private static async Task RecordAsync(WebApplication app, IPluginRegistryPort registry, InstalledPlugin record)
    {
        await registry.UpsertAsync(record, CancellationToken.None).ConfigureAwait(false);

        // A running host picks up new plugins when it restarts; this does the same for the permission snapshot.
        await app.Services.GetRequiredService<ManifestCapabilityPolicyProvider>().RefreshAsync(registry, CancellationToken.None).ConfigureAwait(false);
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "emma-api-tests", Guid.NewGuid().ToString("N"));

    private static async Task<WebApplication> BuildAsync(string root, int perClientPermits, string[] extraArgs, Func<EMMA.Runtime.EmmaRuntimeOptions, EMMA.Runtime.EmmaRuntimeOptions> configure)
    {
        string[] args =
        [
            "--urls=http://127.0.0.1:0",
            $"--Emma:DataDirectory={root}",
            "--Emma:Api:Keys:0:ClientId=alice",
            $"--Emma:Api:Keys:0:KeyHash={ApiKeys.FormatHash(ApiKeys.Hash(AliceKey))}",
            "--Emma:Api:Keys:1:ClientId=bob",
            $"--Emma:Api:Keys:1:KeyHash={ApiKeys.FormatHash(ApiKeys.Hash(BobKey))}",
            $"--Emma:Api:RateLimit:PerClientPermits={perClientPermits}",
            "--Emma:Api:RateLimit:PerClientWindowSeconds=3600",
            "--Logging:LogLevel:Default=None",
            .. extraArgs,
        ];
        WebApplication app = await ApiHost.BuildAsync(args, configure).ConfigureAwait(false);
        await app.StartAsync().ConfigureAwait(false);
        return app;
    }
}
